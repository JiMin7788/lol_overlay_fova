# 00. Project Overview

## 1. 프로젝트 정의

본 프로젝트는 League of Legends 플레이어를 위한 **보조 정보 오버레이 프로그램**이다.
Riot Games가 공식적으로 제공하는 API와, 사용자가 실제로 화면에서 이미 볼 수 있는 정보만을
가공하여 시각적/음성적으로 재제공하는 것을 목표로 한다.

핵심 철학은 다음 한 문장으로 요약된다.

> "새로운 정보를 만들어내지 않는다. 이미 존재하는 정보를 더 빠르고 편하게 전달한다."

## 2. 정책 준수 원칙 (Non-Negotiable Principles)

프로젝트의 모든 모듈은 아래 4가지 원칙을 반드시 준수해야 하며, 각 모듈 명세서의
Acceptance Criteria에는 이 원칙에 대한 체크 항목이 포함된다.

| 원칙 | 설명 |
|---|---|
| P1. 공개 정보 원칙 | 사용자가 이미 화면 또는 공식 API를 통해 획득 가능한 정보만 사용한다. | 패치 의존적 수치(스킬 계수, 아이템/룬 효과, Ability Haste 등)는 전부 Data Dragon, CommunityDragon 기반 M11에서 동적 조회하며, 어떤 모듈에도 하드코딩하지 않는다.
| P2. 비추론 원칙 | 시야 밖 적의 위치를 추론하여 확정 정보처럼 제공하지 않는다 (Last Seen까지만 허용). 사용시각 추정 불가 → 정적 표시만 허용, API 재확인값과 추정값 구분 |
| P3. 비개입 원칙 | 게임 클라이언트 메모리 접근, 코드 주입, 입력 자동화(매크로), 패킷 분석을 수행하지 않는다. |
| P4. 보조 원칙 | 오버레이/음성은 어디까지나 의사결정을 돕는 보조 수단이며, 플레이어의 조작을 대체하지 않는다. |

이 원칙은 `01_SYSTEM_ARCHITECTURE.md`의 데이터 소스 계층 설계와 직접적으로 연결되며,
모든 모듈(M01~M18)은 이 원칙 위반 여부를 Reviewer 체크리스트에서 검증받는다.

## 3. 데이터 소스 (허용된 입력)

- Riot Live Client Data API (localhost)
- Riot Data Dragon (정적 챔피언/아이템/룬 데이터)
- Riot Match-V5 / Spectator API (선택적, 후처리용)
- 화면 캡처: Windows Graphics Capture API, Desktop Duplication API (DXGI)
- OCR (Tab 스코어보드, 아이템 슬롯 등 사용자가 화면에서 직접 보는 영역)
- 사용자 입력: 단축키, 콤보 에디터, 설정값

명시적으로 금지되는 입력: 게임 프로세스 메모리 읽기/쓰기, DLL 인젝션, 네트워크 패킷 스니핑,
자동 입력(매크로), 서드파티 비공식 API.

## 4. 대상 사용자 및 가치 제안

| 사용자 유형 | 제공 가치 |
|---|---|
| 일반 랭크 유저 | 콤보 계산/킬각 판단 자동화로 판단 시간 단축 |
| 정글러 | 정글 경로 예측, 오브젝트 타이머로 동선 최적화 |
| 신규/저티어 유저 | 스킬 빌드/룬/아이템 브리핑으로 학습 곡선 완화 |
| 고티어 유저 | 팀파이트 승률 계산, 상대 파워스파이크 알림으로 미세 우위 확보 |

## 5. 범위 (Scope)

### 5.1 MVP 범위
- Live Client API 연동 (M01)
- 오버레이 렌더링 엔진 (M02)
- 콤보 계산 엔진 (M03)
- TTS 브리핑 (M09)
- 콤보 에디터 (M04)
- 룬 계산 고도화 (M06)
- 아이템 알림 (M07)
- 리콜 타이머 (M08)

### 5.2 V1 이후 범위 (본 문서 세트에서 함께 설계하되 구현은 후순위)
- 정글 추적 (Last Seen 기반)
- STT 질의응답 (M10)
- 챔피언 데이터베이스 고도화 (M11)
- 스펠 쿨타임 정적 계산기 (M12)
- 오브젝트 타이머, 시야 리마인더, 리콜 세이프티, 레인 우선권, CS 포캐스트, 팀파이트 승률 등 고급 분석 기능

### 5.3 명시적 비범위 (Out of Scope)
- 시야 밖 적 위치의 확정적 예측/표시
- 매크로/오토플레이 기능
- 게임 클라이언트 메모리/패킷 조작
- 확률적 승패 예측을 "확정 결과"처럼 제공하는 UI

### 5.4 챔피언 커버리지 전략 (Curation Scaling)

**결정 (2026-07-11 외부 설계 검토 반영):** 목표는 **전 챔피언(약 170종) 정확 지원**이며,
그 수단은 **큐레이션 자동 생성 파이프라인**이다. 챔피언당 `skill_damage` JSON을 수작업으로
만드는 현재 방식은 소수 챔피언에만 적용돼 있고 나머지는 휴리스틱 폴백에 의존하는데,
이 수작업 큐레이션 부채가 확장의 병목이므로 170챔 전부를 이 방식으로 채우지 않는다.

- **목표 상태:** M11 데이터 소스(Data Dragon / CommunityDragon)의 `mStat`/`mStatFormula`,
  스킬 계수 등을 기계적으로 파싱해 `skill_damage` 큐레이션을 자동 생성하고, 사람은 예외
  케이스만 검수/보정한다. 자동 생성 결과는 P1(공개 정보)·하드코딩 금지 원칙과 정합해야 한다.
- **측정된 현재 상태(loop 55, Cowork 실측):** skill_damage 173파일 중 **164개가 손큐레이션 완료**
  (`auto` 플래그 없음), `auto:true`는 **9개만** 잔존 — Aphelios, Ekko, Heimerdinger, Hwei, Illaoi,
  Ivern, Jayce, Ksante, RekSai. 즉 §5.4의 "170챔 정확" 목표는 현 로스터 기준 사실상 도달 상태이며,
  피드백 당시의 "소수만 커버" 전제는 이미 낡았다. 폴백으로 계산된 값은 "근사치"로 표기한다(P2 연장).
- **잔존 9챔의 성격(핵심):** 이들은 나이브한 자동생성이 가장 틀리기 쉬운 부류라 auto-gen이 답이 아니다.
  (a) **구조적 불가** — Aphelios(5무기 Q/E), Illaoi E: 계산 모델로 표현 불가, 정직하게 생략.
  (b) **엔진 데이터모델 한계** — Jayce/RekSai: `ChampionBinParser`가 슬롯당 BIN 스펠 경로를 1개만
  읽어 변신/스탠스 폼(Jayce 망치/대포, RekSai 굴착/비굴착)에 접근 불가 → 엔진 변신지원이 있어야 완결.
  (c) **부분 큐레이션 + auto 유지** — Ekko/Hwei/Illaoi/Ivern/RekSai는 다수 슬롯이 이미 큐레이션됐고
  `auto:true`는 "미해결 슬롯이 하나라도 있음"의 정직한 표시로 남겨둔 것.
  (d) **유일한 순수 raw-auto 데미지 슬롯** — Ksante Q/W/R(하드 큐레이션으로 완결 가능한 단일 후보).
- **결론(방향 수정):** 잔존 목표는 "자동생성 파이프라인"이 아니라 멀티폼/추가스킬 커버리지다. 자동생성기
  (`tools/ChampionDataGen` SkillDamageGen)는 신규 출시 챔피언의 1차 스캐폴딩 용도로만 유지한다.
- **잔존 9챔 구현 방향(loop 56, 사용자 지시 — `docs/modules/M22_MULTIFORM_SKILLS.md`):** 폼/무기/서브스펠을
  각자의 BIN 스펠에서 해석하는 "추가 명명 스킬"을 공통 인에이블러로 두고, Jayce=10스킬 개별계산,
  RekSai/Gnar=폼별 스킬, Hwei=서브스펠 전개, Ksante=콤보 내 R→R 구간 총공세 폼 판정, Ivern·Annie궁 등
  소환스킬=유저 지정 히트수 옵션, Aphelios=무기별 Q5/R5(후순위, 무기가 별도 유닛이라 난이도 높음),
  Illaoi=보류. 단계별 계획은 M22 §Phased implementation plan 참조.
- **범위 배치:** 파이프라인 자체는 V2+ 신규 모듈로 채번한다(`02_DEVELOPMENT_WORKFLOW.md` §8
  로드맵 참조). 그전까지 §7의
  "최소 5개 챔피언" 기준은 정밀 큐레이션 하한선으로만 유지한다.

## 6. 문서 세트 구성

```
docs/
├── 00_PROJECT_OVERVIEW.md          (본 문서)
├── 01_SYSTEM_ARCHITECTURE.md       전체 시스템 구조, 계층, 스레드 모델, 데이터 흐름
├── 02_DEVELOPMENT_WORKFLOW.md      Lead/Agent/Reviewer 루프 프로세스
├── modules/
│   ├── M01_LIVECLIENT_API.md
│   ├── M02_OVERLAY_ENGINE.md
│   ├── M03_COMBO_ENGINE.md
│   ├── M04_COMBO_EDITOR.md
│   ├── M05_DAMAGE_ENGINE.md
│   ├── M06_RUNE_ENGINE.md
│   ├── M07_ITEM_TRACKER.md
│   ├── M08_RECALL_TIMER.md
│   ├── M09_TTS_ENGINE.md
│   ├── M10_STT_ENGINE.md
│   ├── M11_CHAMPION_DATABASE.md
│   ├── M12_SPELL_TIMER.md
│   ├── M13_HOTKEY_MANAGER.md
│   ├── M14_CONFIG_MANAGER.md
│   ├── M15_EVENT_BUS.md
│   ├── M16_RENDER_PIPELINE.md
│   ├── M17_NOTIFICATION_ENGINE.md
│   └── M18_LOGGING.md
├── review/
│   ├── REVIEW_TEMPLATE.md
│   ├── LEAD_GUIDE.md
│   ├── REVIEWER_GUIDE.md
│   └── AGENT_GUIDE.md
└── reports/
    ├── agent_report_template.md
    ├── reviewer_report_template.md
    └── release_checklist.md
```

## 7. 성공 기준 (Project-level Acceptance)

- MVP 8개 모듈이 각각 Reviewer PASS를 받는다.
- 정책 원칙(P1~P4) 위반 사례가 0건이다.
- 콤보 엔진이 최소 5개 챔피언(하드코딩 없이 데이터 기반)에서 정상 동작한다.
- 스펠 쿨타임 계산이 우주적 통찰력/이오니아의 신발 조합 4가지 케이스에서 정확히 동작한다.
- 전체 오버레이 프레임 성능이 기준치(별도 명시, M02 참조) 이내를 유지한다.
