# 01. System Architecture

## 1. 아키텍처 개요

시스템은 5개의 계층(Layer)과 5개의 독립 스레드(Thread)로 구성된다.
모든 계층 간 통신은 **Event Bus(M15)** 를 통한 발행-구독(Pub/Sub) 방식을 원칙으로 하며,
계층 간 직접 참조(tight coupling)는 금지한다.

```
Presentation Layer-Overlay Engine(M02) / Render Pipeline(M16) / Hotkey(M13)Event Bus (M15)Application Layer → Combo Engine(M03) / Combo Editor(M04) / Damage Engine(M05) → Rune Engine(M06) / Item Tracker(M07) / Recall Timer(M08)  →  Spell Timer(M12) / Notification Engine(M17) → Event Bus (M15)Data → Layer-Champion Database(M11) / Config Manager(M14) → Acquisition → Layer-LiveClient API(M01) /OCR / Screen Capture → I/O Layer- TTS Engine(M09) / STT Engine(M10) / Logging(M18)             
```

## 2. 스레드 모델

| Thread | 담당 모듈 | 책임 | 주기 |
|---|---|---|---|
| Thread 1 | M01 LiveClient API | API 폴링, 이벤트 감지, 캐싱 | 0.1s |
| Thread 2 | M02/M16 Overlay/Render | HUD 렌더링, Z-Order 관리 | Vsync (프레임 동기) |
| Thread 3 | OCR (M07 하위) | 화면 버퍼 분석, 스코어보드/아이템 인식 | 이벤트 트리거 시에만 |
| Thread 4 | M09/M10 TTS/STT | 음성 큐, 인식 파이프라인 | 이벤트 큐 대기 |
| Thread 5 | Ring Buffer | 화면 프레임 순환 버퍼 유지 | 지속 |

**설계 원칙**: OCR은 상시 동작하지 않는다. LiveClient API에서 상태 변화(예: 아이템 슬롯 변경,
챔피언 사망)가 감지된 경우에만 Ring Buffer의 최신 프레임을 꺼내 OCR을 수행한다.
이는 성능(CPU) 목표를 달성하기 위한 핵심 설계이며, M07/M01 명세서에서 상세 기술한다.

## 3. Event Bus 설계 (M15)

모든 모듈은 Event Bus를 통해서만 통신한다. 이벤트는 아래 스키마를 따른다.

```
Event {
  id: string (uuid)
  type: string          // e.g. "GAME.ITEM_CHANGED", "COMBO.EXECUTED"
  source: string         // 발행 모듈명
  timestamp: number
  payload: object
}
```

주요 이벤트 네임스페이스:
- `GAME.*` : LiveClient API에서 발생 (챔피언 사망, 아이템 변경, 레벨업 등)
- `COMBO.*` : 콤보 엔진/에디터에서 발생
- `UI.*` : 오버레이/단축키에서 발생
- `VOICE.*` : TTS/STT에서 발생
- `SYSTEM.*` : 설정 변경, 에러, 로깅

Event Bus는 동기(Sync)와 비동기(Async) 구독을 모두 지원해야 하며,
렌더링에 영향을 주는 이벤트(`UI.*`)는 반드시 비동기로 처리하여 프레임 드랍을 방지한다.

## 4. 데이터 모델 개요

### 4.1 Combo Node Graph (M03/M04/M05 공통)

콤보는 챔피언별 하드코딩이 아닌 **노드 그래프**로 표현한다. 각 노드는 다음 공통 속성을 가진다.

```
ComboNode {
  id: string
  nodeType: enum { SKILL, AA, PASSIVE, RUNE, ITEM, EXECUTE }
  name: string
  cooldown: number
  mana: number
  damage: number
  damageType: enum { PHYSICAL, MAGIC, TRUE }
  ratioAD: number
  ratioBonusAD: number
  ratioAP: number
  castTime: number
  delay: number
  travelTime: number
  condition: Condition | null
  stack: number | null
  maxStack: number | null
  executeType: enum { NONE, THRESHOLD, CURRENT_HP, MISSING_HP } 
  executeThreshold: number | null
  priority: number
}
```

**챔피언 특화 속성(Special Property)** 은 별도 Dictionary로 관리하여 기본 속성을 오염시키지 않는다.

```
ChampionSpecialProperty {
  championId: string
  key: string        // e.g. "PassiveStackDamage", "PhenomenalStack"
  valueFormula: string   // 레벨/스택 기반 수식
}
```

이 구조를 통해 신규 챔피언 추가 시 계산 엔진(M03, M05)의 코드 수정 없이
`M11_CHAMPION_DATABASE`의 데이터 갱신만으로 대응 가능해야 한다. 이는 프로젝트의
핵심 비기능 요구사항(확장성)이다.

### 4.2 Damage Pipeline (M05)

```
Input:  ComboNode[], AttackerStat, DefenderStat
Step 1: Base Damage 계산 (ratioAD/AP 적용)
Step 2: Damage Type별 방어관통/방어력 적용 (Armor/MR/True) — 상대 라이너(Defender)의
        실제 Armor/MR 저항 스탯을 반드시 반영하여, 이론적 데미지가 아닌
        "이 상대에게 실제로 들어가는 정확한 데미지"를 산출한다. Defender 스탯의 출처는
        LiveClient API의 상대 플레이어 데이터(공개 범위 내) 및 사용자가 화면에서 확인
        가능한 정보(아이템 등)로 한정한다 (M05 Policy Compliance Checklist 참조).
Step 3: 특수 효과 적용 (Shield, LifeSteal, Critical)
Step 4: Execute 조건 검사 (Threshold / Current HP / Missing HP 기반)
Step 5: 최종 Expected Damage, Remaining HP 산출
Output: { totalDamage, remainingHP, killThreshold, isLethal }
```

### 4.3 Spell Timer 정적 계산 모델 (M12)

M12는 **실시간 쿨타임 추적이 아닌, 정적 쿨타임 계산기**로 한정한다.
변인은 다음 두 가지로 고정하되, 각 변인의 구체 수치(Ability Haste 값)는 **패치에 따라
수시로 변경될 수 있으므로 코드에 하드코딩하지 않고 Riot Data Dragon 기반 M11 Champion
Database에서 매번 동적으로 조회**한다.

- 우주적 통찰력 (룬): Ability Haste (Data Dragon 기준 값, 예: 현재 패치 +18 — 수치는 M11에서 조회)
- 이오니아의 신발 (아이템): Ability Haste (Data Dragon 기준 값, 예: 현재 패치 +10 — 수치는 M11에서 조회)

```
FinalCooldown = BaseCooldown / (1 + TotalAbilityHaste / 100)
TotalAbilityHaste = CosmicInsight(0 or M11.cosmicInsightHaste) + IonianBoots(0 or M11.ionianBootsHaste)
```

계산은 4가지 케이스(둘 다 없음 / 룬만 / 신발만 / 둘 다)로 사전 산출되어 테이블화된다.
탐지는 **Live Client Data API**(플레이어의 룬/아이템 보유 여부 필드)로 수행하며, OCR에
의존하지 않는다.

중요한 정책 경계: 실제 "언제 스킬을 썼는지"는 시스템이 확인할 방법이 없으므로, **이를
추정하여 감소하는 실시간 카운트다운은 어떤 모듈에서도 구현하지 않는다.** M12가 산출한
쿨타임 값은 사용자가 점수판(Tab)을 열었을 때 또는 오버레이 설정을 활성화했을 때 정적으로만
표시되며, Notification Engine(M17) 또한 이 값에 대해 자체적으로 흐르는 타이머를 제공하지
않는다 (M17은 API가 직접·지속적으로 값을 재확인해주는 항목, 예: Recall/Death Timer에
대해서만 진행 표시를 제공한다). 이 경계는 M12/M17 명세서에서 명확히 재정의한다.

## 5. UI 구조

```
Dashboard
├── Champion
├── Combo Manager
│    ├── Combo A / B / C ...
│    └── Import / Export
├── Voice Assistant (TTS/STT)
├── Overlay
├── Jungle Tracking
├── Rune Calculator
├── OCR
├── API Monitor
├── Performance
└── Settings
```

단축키 기반 콤보 표시(HUD)는 3~5초 표시 후 자동 숨김을 기본값으로 하며,
Config Manager(M14)를 통해 사용자가 조정 가능해야 한다.

## 6. 비기능 요구사항 (Non-Functional Requirements)

| 항목 | 목표 |
|---|---|
| API 폴링 지연 | ≤ 0.1s |
| 오버레이 렌더링 | Vsync 동기, 프레임 드랍 없음 |
| OCR 실행 빈도 | 이벤트 트리거 시에만 (상시 X) |
| 메모리 사용량 | 별도 벤치마크 문서에서 정의 (M02/M16 참조) |
| 확장성 | 신규 챔피언 추가 시 코드 수정 없이 데이터 추가만으로 대응 |
| 정책 준수 | P1~P4 원칙 100% 준수 (00_PROJECT_OVERVIEW.md 참조) |
| 렌더 스레드 할당 | 프레임당 힙 할당 0건 (per-frame heap allocation은 결함으로 간주) |

## 7. 보안/정책 경계 (Architecture-level Guardrail)

- Acquisition Layer(M01, OCR, Screen Capture)는 **읽기 전용**이며, 어떠한 모듈도
  게임 프로세스에 쓰기 작업을 수행하지 않는다.
- Application Layer는 Acquisition Layer가 제공한 데이터 범위를 벗어난 추론(예: 시야 밖 위치 확정)을
  수행할 수 없다. Last Seen 개념(마지막 관측 시각/위치 표시)까지만 허용된다.
- 모든 모듈 명세서(M01~M18)는 "정책 준수 체크리스트" 섹션을 필수로 포함해야 하며,
  Reviewer는 이 섹션을 근거로 PASS/FAIL을 판정한다 (02_DEVELOPMENT_WORKFLOW.md 참조).

## 8. 운영 리스크 및 의존성 (Operational Risks) — 2026-07-11 외부 설계 검토

### 8.1 검토가 확인한 설계 강점 (유지 대상)
정책 P1~P4가 장식이 아니라 코드 구조에 실제로 박혀 있다는 점이 이 프로젝트의 핵심 자산이다.
TargetHealthTracker가 적 체력을 "하한 추정치"로만 취급하고 확정값처럼 노출하지 않는 것,
매핑되지 않은 BIN 스탯 id를 0으로 뭉개지 않고 예외를 던져 "틀린 그럴듯한 숫자"를 차단하는 것 —
킬각 계산기의 최대 리스크(틀린 확신 제공)를 구조적으로 막는 이 설계 판단은 회귀 시 결함으로 본다.
공식 API만 사용하는 현재의 "깨끗한" 데이터 소싱도 아래 8.3 리스크에 대한 최대 방어선이다.

### 8.2 배포/런타임 마찰 (제품화 장벽)
개발 중 반복해서 발목을 잡은 항목들이며, 일반 사용자에게는 더 큰 진입 장벽이 된다.
설치/실행 UX 설계 시 아래를 명시적 목표로 다룬다.

| 리스크 | 영향 | 완화 방향 |
|---|---|---|
| 관리자 권한(UAC) 요구 | 실행 때마다 마찰, 신뢰 저하 | 정말 필요한 권한인지 재검증; 불필요하면 제거, 필요하면 매니페스트+1회 안내 |
| topmost 재주장 싸움 | 다른 창과 z-order 경합 | 재주장 주기/트리거를 명세화하고 과도한 폴링 제거 |
| 빌드 배포 시 DLL 잠금 | 실행 중 파일 교체 실패 | 업데이트 시 프로세스 종료→교체 절차 또는 side-by-side 배포 |

### 8.3 Vanguard/안티치트 정책 의존 리스크
현재는 **공식 API(127.0.0.1:2999 Live Client, ddragon/CDN)만** 사용해 안티치트 관점에서
깨끗하다(§7의 읽기 전용·비개입 경계가 이를 보장). 다만 Riot Vanguard의 정책·탐지 범위 변화는
프로젝트 통제 밖의 상존 리스크다. 대응 원칙: (1) 어떤 경우에도 §7 경계(메모리 접근/주입/입력
자동화 금지)를 넘지 않는다 — 이 선을 지키는 한 정책 변화 노출이 최소화된다. (2) 오버레이
표시 방식이 안티치트에 오탐될 여지가 생기면 기능을 축소할지언정 경계를 넘지 않는다.
