using System;
using System.Collections.Generic;
using Overlay.Core.ChampionDb;

namespace Overlay.Client;

/// <summary>
/// Lightweight in-memory i18n helper for the home app. Holds the current UI language, a
/// ko/en string table, and a <see cref="LanguageChanged"/> event that views subscribe to so
/// visible text updates live (no app restart). Not a full localization framework — it covers
/// the home strings this UI owns; unlocalized lookups fall back to the key. Language codes are
/// normalized to <see cref="Lang.Ko"/>/<see cref="Lang.En"/>; persistence to config
/// (<c>general.language</c>) is the caller's job (Settings view / HomeWindow startup).
/// </summary>
public static class Localization
{
    public enum Lang { Ko, En }

    /// <summary>Current UI language. Defaults to Korean (this app is Korean-first; English is
    /// the opt-in option). Change via <see cref="SetLanguage(Lang)"/> / <see cref="ApplyCode"/>.</summary>
    public static Lang CurrentLanguage { get; private set; } = Lang.Ko;

    /// <summary>Raised after the language changes so subscribed views can re-apply their text.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Maps a persisted config code to a <see cref="Lang"/>. English is selected only by
    /// the canonical code this app writes ("en" / "english"); every other value — including the
    /// empty/unknown case and the <see cref="Overlay.Core.Config.ConfigSchema"/> default "en-US" —
    /// keeps the Korean-first default, so an untouched install comes up in Korean and English is an
    /// explicit opt-in made in Settings.</summary>
    public static Lang Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Lang.Ko;
        var c = code.Trim();
        return c.Equals("en", StringComparison.OrdinalIgnoreCase)
            || c.Equals("english", StringComparison.OrdinalIgnoreCase)
            ? Lang.En : Lang.Ko;
    }

    /// <summary>The canonical config string for the current language ("ko" / "en").</summary>
    public static string ToCode(Lang lang) => lang == Lang.En ? "en" : "ko";

    /// <summary>Applies a language parsed from a config code without re-persisting. Raises
    /// <see cref="LanguageChanged"/> only when the value actually changes.</summary>
    public static void ApplyCode(string? code) => SetLanguage(Parse(code));

    public static void SetLanguage(Lang lang)
    {
        if (lang == CurrentLanguage) return;
        CurrentLanguage = lang;
        LanguageChanged?.Invoke();
    }

    /// <summary>Looks up <paramref name="key"/> in the current language table; falls back to the
    /// English table, then to the raw key so a missing string is visible but never crashes.</summary>
    public static string L(string key)
    {
        var table = CurrentLanguage == Lang.En ? En : Ko;
        if (table.TryGetValue(key, out var value)) return value;
        return En.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>Convenience: <see cref="L(string)"/> then <see cref="string.Format(string, object[])"/>.</summary>
    public static string F(string key, params object[] args) => string.Format(L(key), args);

    /// <summary>Localized display name for a champion id. Ids stay canonical ("Aatrox") everywhere in
    /// the data/config; this only affects what the UI shows. Primary source is the Core-side
    /// <see cref="ChampionLocalizationRepository"/> (dynamically fetched from CommunityDragon at
    /// startup — see <c>AppComposition.InitializeAsync</c> — covering the full cached ~173-champion
    /// roster); the small hand-typed <see cref="ChampionNamesKo"/> table is only a last-resort
    /// fallback for when that fetch hasn't completed/failed (e.g. an offline first run before any
    /// cache exists). English (and any id unresolved by either source) falls back to the canonical
    /// id, which is already the English display name.</summary>
    public static string ChampionName(string championId)
    {
        if (string.IsNullOrEmpty(championId)) return championId;
        if (CurrentLanguage == Lang.Ko)
        {
            if (ChampionLocalizationRepository.IsInitialized
                && ChampionLocalizationRepository.Get(championId) is string dynamicKo)
                return dynamicKo;
            if (ChampionNamesKo.TryGetValue(championId, out var ko))
                return ko;
        }
        return championId;
    }

    /// <summary>Last-resort Korean display names, used only when
    /// <see cref="ChampionLocalizationRepository"/> is uninitialized or has no entry for the id
    /// (see <see cref="ChampionName"/>). Kept small deliberately — the dynamic CommunityDragon
    /// fetch is the primary source for the full cached champion set.</summary>
    private static readonly Dictionary<string, string> ChampionNamesKo = new(StringComparer.Ordinal)
    {
        ["Aatrox"] = "아트록스",
        ["Ahri"] = "아리",
        ["Annie"] = "애니",
        ["Zed"] = "제드",
        ["Jinx"] = "징크스",
    };

    /// <summary>Localized display name for an item. Mirrors <see cref="ChampionName"/>'s shape:
    /// returns Data Dragon's ko_KR name (<see cref="ItemData.NameKo"/>) when in Korean mode and
    /// available, else the canonical English name — today's behavior when the ko_KR file hasn't
    /// been fetched yet (e.g. offline first run) or the item predates it in an old cache.</summary>
    public static string ItemName(ItemData item)
        => CurrentLanguage == Lang.Ko && item.NameKo is not null ? item.NameKo : item.Name;

    private static readonly Dictionary<string, string> Ko = new()
    {
        // ── Nav / shell ──────────────────────────────────────────────
        ["nav.home"] = "홈",
        ["nav.search"] = "유저검색",
        ["nav.combo"] = "콤보설정",
        ["nav.settings"] = "설정",
        ["nav.help"] = "도움말",
        ["nav.stats"] = "통계",
        ["stats.title"] = "라인별 챔피언 티어",
        ["stats.meta"] = "{0} 기준 · {1} 패치 · 한국 · {2:N0} 매치 표본 (자체 수집)",
        ["stats.meta.time"] = "yyyy년 M월 d일 HH:mm",
        ["stats.note.pick"] = "라인별 픽률 {0} 이상만 표시",
        ["stats.bracket.thin"] = "{0} (표본 얇음)",
        ["stats.note.thin"] = "이 구간은 아직 표본이 얇습니다 — {0} 매치, 30판 이상인 챔피언 {1}명. 수집이 쌓이면 채워집니다.",
        ["stats.grade.gated"] = "표본이 얇아 한 단계 내려간 항목입니다. 점수(점 추정)는 위 컷이지만, 표준오차를 감안한 PS★가 {0}이라 그 등급을 표본이 뒷받침하지 못합니다.",
        ["stats.empty"] = "통계 데이터가 아직 없습니다. 수집/집계 파이프라인이 tiers.json을 만들면 표시됩니다.",
        ["stats.none"] = "조건에 맞는 챔피언이 없습니다.",
        ["stats.sort.grade"] = "등급순",
        ["stats.sort.win"] = "승률순",
        ["stats.sort.pick"] = "픽률순",
        ["stats.sort.ban"] = "밴률순",
        ["stats.sort.games"] = "표본순",
        ["stats.col.rank"] = "#",
        ["stats.col.champion"] = "챔피언",
        ["stats.col.games"] = "표본",
        ["stats.col.sample"] = "표본수",
        ["stats.col.grade"] = "등급",
        ["stats.col.win"] = "승률",
        ["stats.col.score"] = "점수",
        ["stats.col.pick"] = "픽률",
        ["stats.col.pick.role"] = "픽률(라인)",
        ["stats.col.ban"] = "밴률",
        ["stats.col.ban.all"] = "밴률(전체)",
        ["stats.col.lt25"] = "<25분",
        ["stats.col.mid"] = "25–32분",
        ["stats.col.gt32"] = ">32분",
        ["stats.col.durgraph"] = "시간대 승률",
        ["stats.dur.tip"] = "{0} 승률 {1} ({2}판)",
        ["stats.dur.empty"] = "{0} 표본 없음",
        ["stats.col.favor"] = "유리",
        ["stats.col.unfavor"] = "불리",
        ["stats.counter.tip"] = "vs {0} · 승률 {1} ({2}판)",
        ["stats.counter.headerTip"] = "이 라인에서 상대했을 때 승률이 가장 높은/낮은 챔피언입니다. 표본을 확보하려고 수집된 전 티어를 합산하므로 위의 티어(브라켓) 필터와 무관하게 전체 래더 기준입니다.",

        // ── Statistics filters (loop 462) ────────────────────────────
        ["stats.filter.search"] = "챔피언 검색",
        ["stats.filter.searchHint"] = "검색",
        ["stats.grade.tip"] = "점수는 승률·픽률·밴율을 종합한 PS 스타일 지표입니다(50 근처가 평균). 표본으로 보정한 뒤 표준오차를 감안한 하한 PS★를 기준으로 등급을 매기며, 컷은 S+ 56 / S 53 / A 50.5 / B 48 / C 45.5, 그 아래가 D입니다. 절대 기준이라 라인·패치에 따라 S+가 없을 수도, 여럿일 수도 있습니다. 표본이 얇으면 오차가 커져 PS★가 내려가므로 30판 60% 같은 항목은 자연히 아래 등급이 됩니다. (국내 티어사이트 258챔 표본에 맞춰 보정한 자체 모형)",
        ["stats.filter.all"] = "전체",
        ["stats.filter.sample"] = "표본 {0}+",
        ["stats.filter.pick"] = "픽률 {0}+",
        ["stats.filter.tierTip"] = "수집 시드 플레이어의 티어 기준입니다. 랭크 매칭이 비슷한 티어끼리 붙이지만 그 판의 티어와 같다고 보장하진 않습니다. 티어마다 같은 양을 수집하므로 '전체 티어'는 실제 인구 분포가 아니라 수집 할당의 합입니다.",
        ["stats.filter.pickTip"] = "라인을 고르면 픽률 분모가 '전체 매치'에서 '그 라인 슬롯'으로 바뀝니다. 밴은 라인이 정해지기 전에 이뤄지므로 밴률은 항상 전체 기준입니다.",
        ["stats.role.TOP"] = "탑",
        ["stats.role.JUNGLE"] = "정글",
        ["stats.role.MIDDLE"] = "미드",
        ["stats.role.BOTTOM"] = "원딜",
        ["stats.role.UTILITY"] = "서폿",
        ["stats.role.UNKNOWN"] = "미분류",
        ["stats.tier.IRON"] = "아이언",
        ["stats.tier.BRONZE"] = "브론즈",
        ["stats.tier.SILVER"] = "실버",
        ["stats.tier.GOLD"] = "골드",
        ["stats.tier.PLATINUM"] = "플래티넘",
        ["stats.tier.EMERALD"] = "에메랄드",
        ["stats.tier.DIAMOND"] = "다이아",
        ["stats.tier.MASTER"] = "마스터",
        ["stats.tier.GRANDMASTER"] = "그랜드마스터",
        ["stats.tier.CHALLENGER"] = "챌린저",
        ["stats.tier.UNKNOWN"] = "티어 미상",

        // Cumulative brackets (loop 463) — the aggregation's directory slugs.
        ["stats.bracket.all"] = "전체 티어",
        ["stats.bracket.challenger"] = "챌린저",
        ["stats.bracket.grandmaster_plus"] = "그랜드마스터+",
        ["stats.bracket.master_plus"] = "마스터+",
        ["stats.bracket.diamond_plus"] = "다이아+",
        ["stats.bracket.emerald_plus"] = "에메랄드+",
        ["stats.bracket.platinum_plus"] = "플래티넘+",
        ["stats.bracket.gold_plus"] = "골드+",
        ["stats.bracket.gold_minus"] = "골드-",
        ["stats.bracket.silver_minus"] = "실버-",
        ["stats.bracket.bronze_minus"] = "브론즈-",
        ["stats.bracket.iron"] = "아이언",
        ["rec.bracket.tip"] = "추천을 뽑아올 티어 구간입니다. '플래티넘+'는 플래티넘부터 챌린저까지를 합친 표본입니다.",

        // ── Help / manual (2026-07-25) ───────────────────────────────
        ["help.page.subtitle"] = "각 기능이 무엇을 읽고 무엇을 하지 않는지 정리했습니다.",
        ["help.intro.title"] = "Fova 소개",
        ["help.intro.body"] = "Fova는 리그 오브 레전드 데스크톱 컴패니언입니다. 공개 데이터만 읽고(메모리 접근·입력 자동화·패킷 조작 없음), 화면 표시와 정보 제공만 하며, 게임 내 행동은 항상 유저가 직접 합니다.",
        ["help.overlay.title"] = "인게임 오버레이",
        ["help.overlay.body"] = "게임 위에 클릭스루(마우스 통과) 창으로 정보를 표시합니다. Shift+Tab으로 표시/숨김을 전환하고, 설정의 배율·이동 모드로 위치와 크기를 조절할 수 있습니다.",
        ["help.combo.title"] = "콤보 데미지 계산기",
        ["help.combo.body"] = "콤보설정에서 챔피언별 스킬 콤보를 만들면, 게임 중 현재 대상 기준 예상 데미지를 실시간 계산해 보여줍니다. Alt+1 콤보 오버레이, Alt+2 스킬 데미지 오버레이를 전환하며, 상단 적 초상화를 탭해 대상을 지정합니다.",
        ["help.timers.title"] = "타이머 · 알림",
        ["help.timers.body"] = "적 귀환·부활, 억제기·넥서스 포탑 리스폰 타이머를 표시하고 아이템 완성 알림을 제공합니다. 분:초 표기는 설정에서 전환합니다.",
        ["help.minimap.title"] = "미니맵 비전 (기본 꺼짐)",
        ["help.minimap.body"] = "본인 화면의 미니맵 영역만 캡처해 이미 보이는 적 아이콘을 인식하고, 적 발견/사라짐 음성 안내와 마지막 위치 잔상을 제공합니다. 시야 밖 추론은 하지 않으며, 저신뢰 감지는 자동 필터링됩니다.",
        ["help.champselect.title"] = "픽창 도우미 · 룬 에디터",
        ["help.champselect.body"] = "픽창에 들어가면 대시보드가 앞으로 나오고 현재 사용 중인 룬 페이지가 표시됩니다. 룬을 클릭하면 롤 클라이언트에 실시간 반영되고, [적용]은 선택한 프리셋 전체 적용, [현재 룬+스펠 저장]은 프리셋 저장, 오른쪽 레일에서 추천 룬을 골라볼 수 있습니다. 자동 적용은 설정에서 옵트인해야만 동작합니다.",
        ["help.comp.title"] = "픽/밴 · 조합 분석",
        ["help.comp.body"] = "픽창에서 양 팀 확정 픽과 밴을 보여주고, 라이엇 공식 성향 점수로 팀별 물리/마법 비율과 트루 데미지 보유 챔피언 수를 요약합니다. 적 조합에 따라 파편 힌트를 제안합니다(자동 변경 없음).",
        ["help.spells.title"] = "스펠 · 점멸 키",
        ["help.spells.body"] = "첫 실행 시 점멸을 D/F 중 어느 키에 둘지 한 번 묻고, 이후 프리셋 적용 시 점멸이 항상 그 키로 가도록 정렬합니다. [스펠 ⇄] 버튼으로 현재 스펠 위치를 즉시 교체할 수 있으며, 설정에서 점멸 키를 언제든 바꿀 수 있습니다.",
        ["help.wards.title"] = "적 제어 와드 현황",
        ["help.wards.body"] = "적이 소지 중인 제어 와드 개수를 보여줍니다(공개 스코어보드 데이터). 이번 시즌의 전용 와드 슬롯(V)에 든 와드는 게임이 외부에 공개하지 않아 개수에 포함되지 않습니다 — 표시값은 일반 인벤토리 기준입니다.",
        ["help.search.title"] = "유저검색",
        ["help.search.body"] = "소환사명으로 전적·프로필을 조회합니다.",
        ["help.data.title"] = "데이터 출처 · 광고",
        ["help.data.body"] = "Data Dragon, CommunityDragon, 로컬 라이브 클라이언트 API, 로컬 LCU API만 사용합니다. 룬 추천은 랭크 경기 데이터를 집계한 통계이며 개인 데이터는 배포되지 않습니다. 광고는 데스크톱 홈 화면 한 곳뿐이며 게임 중에는 완전히 비활성화됩니다.",

        // ── Settings: spells ─────────────────────────────────────────
        ["spell.1"] = "정화",
        ["spell.3"] = "탈진",
        ["spell.4"] = "점멸",
        ["spell.6"] = "유체화",
        ["spell.7"] = "회복",
        ["spell.11"] = "강타",
        ["spell.12"] = "순간이동",
        ["spell.14"] = "점화",
        ["spell.21"] = "방어막",
        ["spell.swap"] = "스펠 위치 교체",
        ["spell.change"] = "클릭해서 스펠 변경",
        ["ward.title"] = "제어 와드 수",
        ["rec.title"] = "추천 룬",
        ["rec.samples"] = "표본",
        ["rec.pickRate"] = "픽률",
        ["rec.winRate"] = "승률",
        // "조합"이지 "순서"가 아님 — 집계는 최종 인벤토리 기반(타임라인 미수집)이라 빌드 순서를 모른다.
        ["rec.items.title"] = "코어 조합 ({0})",
        ["rec.items.title.single"] = "추천 아이템 ({0})",
        ["rec.items.boots"] = "신발",
        ["settings.flashKey"] = "점멸 키",
        ["settings.flashKey.desc"] = "프리셋 적용 시 점멸을 이 키로 정렬합니다",
        ["settings.flashKey.d"] = "D 점멸",
        ["settings.flashKey.f"] = "F 점멸",
        ["header.previewShow"] = "오버레이 미리보기",
        ["header.previewHide"] = "오버레이 숨기기",
        ["status.waiting"] = "게임 대기 중",
        ["status.detected"] = "게임 감지됨",

        // ── Home / dashboard ─────────────────────────────────────────
        ["home.title"] = "대시보드",
        ["home.subtitle"] = "게임 상태와 저장된 설정을 한눈에 확인하세요.",
        ["home.gameState"] = "게임 상태",
        ["home.savedCombos"] = "저장된 콤보",
        ["home.liveStats"] = "실시간 통계",
        ["home.emptyState"] = "게임에 입장하면 실시간 통계가 표시됩니다.",
        ["home.champion"] = "챔피언",
        ["home.level"] = "레벨",
        ["home.gold"] = "보유 골드",
        ["home.ad"] = "AD",
        ["home.ap"] = "AP",
        ["home.idle"] = "대기 중",
        ["home.detected"] = "게임 감지됨",

        // ── User search ──────────────────────────────────────────────
        ["search.title"] = "유저검색",
        ["search.placeholder"] = "소환사명 입력",
        ["search.button"] = "검색",
        ["search.help"] = "게임 중 소환사명으로 같은 게임의 플레이어를 검색합니다. 외부 전적 검색은 향후 지원 예정 (API 키 필요).",
        ["search.enterName"] = "소환사명을 입력하세요.",
        ["search.noGame"] = "현재 진행 중인 게임이 없습니다. ",
        ["search.resultCount"] = "'{0}' 검색 결과 {1}명",
        ["search.noMatch"] = "'{0}'와(과) 일치하는 플레이어가 이 게임에 없습니다.",
        ["search.blue"] = "블루팀",
        ["search.red"] = "레드팀",
        ["search.dead"] = "사망",
        ["search.unknown"] = "(알 수 없음)",

        // ── Settings ─────────────────────────────────────────────────
        ["settings.title"] = "설정",
        ["settings.language"] = "언어",
        ["settings.languageDesc"] = "앱 표시 언어를 선택합니다. 변경 시 즉시 적용됩니다.",
        ["settings.korean"] = "한국어",
        ["settings.english"] = "English",

        // ── Combo settings ───────────────────────────────────────────
        ["combo.title"] = "콤보설정",
        ["combo.build"] = "콤보 만들기",
        ["combo.champion"] = "챔피언",
        ["combo.championSelect"] = "챔피언 선택",
        ["combo.championSearch"] = "챔피언 검색",
        ["combo.palette"] = "스킬 팔레트",
        ["combo.paletteSkills"] = "스킬",
        ["combo.paletteMore"] = "아이템",
        ["combo.paletteHint"] = "챔피언을 선택하면 스킬이 표시됩니다.",
        ["combo.itemSearch"] = "아이템 검색으로 추가",
        ["combo.itemHint"] = "선택한 아이템은 현재 실시간 스탯 위에 가상으로 더하는 가상의 빌드(추정치)이며, 실제 게임 상태를 대체하지 않습니다.",
        ["combo.itemRemove"] = "제거: {0}",
        ["combo.dragHint"] = "스킬을 아래로 드래그해 순서에 추가하세요.",
        ["combo.name"] = "콤보 이름",
        ["combo.nameHint"] = "예: 올인 콤보",
        ["combo.hotkey"] = "단축키 (선택)",
        ["combo.setHotkey"] = "단축키 설정",
        ["combo.pressKey"] = "단축키를 입력해주세요…",
        ["combo.hotkey2"] = "두 번째 단축키 (선택)",
        ["combo.sequence"] = "콤보 순서",
        ["combo.sequenceEmpty"] = "여기로 스킬을 드래그하세요. 위로 드래그하면 삭제됩니다.",
        ["combo.clear"] = "순서 비우기",
        ["combo.save"] = "저장",
        ["combo.saved"] = "저장된 콤보",
        ["combo.emptySaved"] = "아직 저장된 콤보가 없습니다.",
        ["combo.savedHint"] = "게임 입장 후 등록한 단축키를 누르면 콤보 데미지가 오버레이에 표시됩니다.",
        ["combo.noHotkey"] = "단축키 미지정",
        ["combo.hotkeyLabel"] = "단축키: {0}",
        ["combo.delete"] = "삭제",
        ["combo.noName"] = "(이름 없음)",
        ["combo.initializing"] = "데이터 초기화 중입니다. 잠시 후 다시 시도하세요.",
        ["combo.selectChampion"] = "챔피언을 선택하세요.",
        ["combo.enterName"] = "콤보 이름을 입력하세요.",
        ["combo.addSkill"] = "콤보 순서에 스킬을 하나 이상 추가하세요.",
        ["combo.saveFailed"] = "저장 실패: {0}",
        ["combo.saveOk"] = "'{0}' 콤보를 저장했습니다.",
        ["combo.skillLoadFailed"] = "스킬을 불러오지 못했습니다: {0}",

        // ── Defender target snapshot (가상 타겟) ─────────────────────
        ["combo.copyTargetStats"] = "타겟 스탯 복사",
        ["combo.useSnapshotTarget"] = "가상 타겟 사용",
        ["combo.snapshotNone"] = "캡처된 타겟 없음",
        ["combo.snapshotCaptured"] = "{0} · 방어력 {1} · 마저 {2} ({3} 전 캡처)",
        ["combo.snapshotCaptureOk"] = "타겟 스탯을 복사했습니다.",
        ["combo.snapshotCaptureFailed"] = "실제 타겟이 없어 복사하지 못했습니다.",

        // ── Manual bonus effects (추가효과) ──────────────────────────
        ["combo.addFx"] = "추가효과 붙이기",
        ["combo.removeFx"] = "추가효과 제거: {0}",
        ["combo.fxOnHit"] = "온히트",
        ["combo.fxOnAbility"] = "스킬적중",
        ["combo.fxSelf"] = "패시브",
        ["combo.itemDetach"] = "아이템 분리: {0}",
        ["combo.hitDuration"] = "적중시간 설정 (최대 {0}초)",

        // ── Combo target ─────────────────────────────────────────────
        ["target.title"] = "콤보 대상",
        ["target.auto"] = "자동 (같은 라인 상대)",
        ["target.hint"] = "자동은 같은 라인 상대를 노립니다. 특정 챔피언을 선택하면 그 대상으로 계산합니다.",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["nav.home"] = "Home",
        ["nav.search"] = "User Search",
        ["nav.combo"] = "Combos",
        ["nav.settings"] = "Settings",
        ["nav.help"] = "Help",
        ["nav.stats"] = "Statistics",
        ["stats.title"] = "Champion tier list by lane",
        ["stats.meta"] = "as of {0} · patch {1} · KR · {2:N0} match sample (self-collected)",
        ["stats.meta.time"] = "d MMM yyyy HH:mm",
        ["stats.note.pick"] = "showing champions above {0} pick rate in the lane",
        ["stats.bracket.thin"] = "{0} (thin)",
        ["stats.note.thin"] = "This band is still thin — {0} matches, {1} champions with 30+ games. It fills in as collection accumulates.",
        ["stats.grade.gated"] = "Held one grade down by its sample. The point score clears the higher cutoff, but the SE-adjusted PS★ is {0}, so the sample does not support that grade.",
        ["stats.empty"] = "No statistics yet. This table appears once the collection/aggregation pipeline emits tiers.json.",
        ["stats.none"] = "No champion matches these filters.",
        ["stats.sort.grade"] = "By grade",
        ["stats.sort.win"] = "By win rate",
        ["stats.sort.pick"] = "By pick rate",
        ["stats.sort.ban"] = "By ban rate",
        ["stats.sort.games"] = "By sample",
        ["stats.col.rank"] = "#",
        ["stats.col.champion"] = "Champion",
        ["stats.col.games"] = "Games",
        ["stats.col.sample"] = "Sample",
        ["stats.col.grade"] = "Grade",
        ["stats.col.win"] = "Win",
        ["stats.col.score"] = "Score",
        ["stats.col.pick"] = "Pick",
        ["stats.col.pick.role"] = "Pick (lane)",
        ["stats.col.ban"] = "Ban",
        ["stats.col.ban.all"] = "Ban (all)",
        ["stats.col.lt25"] = "<25min",
        ["stats.col.mid"] = "25–32min",
        ["stats.col.gt32"] = ">32min",
        ["stats.col.durgraph"] = "Win rate by length",
        ["stats.dur.tip"] = "{0} win rate {1} ({2} games)",
        ["stats.dur.empty"] = "{0} no games",
        ["stats.col.favor"] = "Favorable",
        ["stats.col.unfavor"] = "Unfavorable",
        ["stats.counter.tip"] = "vs {0} · {1} win rate ({2} games)",
        ["stats.counter.headerTip"] = "The lane opponents this champion beats most / least. To reach a usable sample these pool every collected tier, so unlike the tier list above they are ladder-wide and do not follow the bracket filter.",

        // ── Statistics filters (loop 462) ────────────────────────────
        ["stats.filter.search"] = "Find champion",
        ["stats.filter.searchHint"] = "Search",
        ["stats.grade.tip"] = "The score is a PS-style index combining win, pick and ban rate (≈50 is average). Grades are cut on PS★ — that score sample-shrunk, then dropped by its standard error — with cutoffs S+ 56 / S 53 / A 50.5 / B 48 / C 45.5, below which D. They are absolute, so a lane can have no S+ at all or several. A thin sample has a larger error, so its PS★ falls and a 30-game 60% pick lands a tier lower on its own. (Our own model, calibrated to a 258-champion snapshot of a Korean tier site.)",
        ["stats.filter.all"] = "All",
        ["stats.filter.sample"] = "{0}+ games",
        ["stats.filter.pick"] = "{0}+ pick",
        ["stats.filter.tierTip"] = "Based on the tier of the player each match was collected through. Ranked matchmaking keeps a game close to its seed but does not guarantee the game itself sat at that tier. Every tier gets the same collection quota, so \"All tiers\" is the sum of those quotas, not the real player distribution.",
        ["stats.filter.pickTip"] = "Choosing a lane switches the pick-rate denominator from all matches to the slots at that position. Bans happen before positions exist, so ban rate is always the champion-wide figure.",
        ["stats.role.TOP"] = "Top",
        ["stats.role.JUNGLE"] = "Jungle",
        ["stats.role.MIDDLE"] = "Mid",
        ["stats.role.BOTTOM"] = "Bot",
        ["stats.role.UTILITY"] = "Support",
        ["stats.role.UNKNOWN"] = "Unassigned",
        ["stats.tier.IRON"] = "Iron",
        ["stats.tier.BRONZE"] = "Bronze",
        ["stats.tier.SILVER"] = "Silver",
        ["stats.tier.GOLD"] = "Gold",
        ["stats.tier.PLATINUM"] = "Platinum",
        ["stats.tier.EMERALD"] = "Emerald",
        ["stats.tier.DIAMOND"] = "Diamond",
        ["stats.tier.MASTER"] = "Master",
        ["stats.tier.GRANDMASTER"] = "Grandmaster",
        ["stats.tier.CHALLENGER"] = "Challenger",
        ["stats.tier.UNKNOWN"] = "Unknown tier",

        // Cumulative brackets (loop 463) — the aggregation's directory slugs.
        ["stats.bracket.all"] = "All tiers",
        ["stats.bracket.challenger"] = "Challenger",
        ["stats.bracket.grandmaster_plus"] = "Grandmaster+",
        ["stats.bracket.master_plus"] = "Master+",
        ["stats.bracket.diamond_plus"] = "Diamond+",
        ["stats.bracket.emerald_plus"] = "Emerald+",
        ["stats.bracket.platinum_plus"] = "Platinum+",
        ["stats.bracket.gold_plus"] = "Gold+",
        ["stats.bracket.gold_minus"] = "Gold-",
        ["stats.bracket.silver_minus"] = "Silver-",
        ["stats.bracket.bronze_minus"] = "Bronze-",
        ["stats.bracket.iron"] = "Iron",
        ["rec.bracket.tip"] = "Which tier band the recommendations come from. \"Platinum+\" is Platinum through Challenger, pooled.",

        // ── Help / manual (2026-07-25) ───────────────────────────────
        ["help.page.subtitle"] = "What each feature reads, and what it deliberately does not do.",
        ["help.intro.title"] = "About Fova",
        ["help.intro.body"] = "Fova is a League of Legends desktop companion. It reads public data only (no memory access, no input automation, no packet manipulation), displays information, and never acts in game on your behalf.",
        ["help.overlay.title"] = "In-game overlay",
        ["help.overlay.body"] = "A click-through window drawn over the game. Toggle with Shift+Tab; adjust scale and positions in Settings / move mode.",
        ["help.combo.title"] = "Combo damage calculator",
        ["help.combo.body"] = "Build per-champion skill combos in the Combos view; in game the overlay shows the expected damage against the current target from your live stats. Alt+1 toggles the combo overlay, Alt+2 the skill-damage overlay; tap an enemy portrait to set the target.",
        ["help.timers.title"] = "Timers & alerts",
        ["help.timers.body"] = "Enemy recall/respawn, inhibitor and nexus-turret respawn timers, and item-completion alerts. Switch mm:ss formatting in Settings.",
        ["help.minimap.title"] = "Minimap vision (off by default)",
        ["help.minimap.body"] = "Captures only the minimap region of YOUR OWN screen to recognize enemy icons already shown to you, with appear/vanish voice callouts and last-seen ghost markers. No fog-of-war inference; low-confidence detections are filtered automatically.",
        ["help.champselect.title"] = "Champ select assistant & rune editor",
        ["help.champselect.body"] = "Entering champ select raises the dashboard and shows your CURRENT rune page. Clicking runes applies changes to the client in real time; [적용] applies the selected preset, [현재 룬+스펠 저장] saves one, and the right rail lists recommendations to browse. Auto-apply only runs if you opt in via Settings.",
        ["help.comp.title"] = "Picks/bans & composition analysis",
        ["help.comp.body"] = "Shows both teams' locked picks and bans with a physical/magic share summary from Riot's official style scores plus a true-damage count, and suggests a shard hint against the enemy comp (nothing is changed automatically).",
        ["help.spells.title"] = "Spells & Flash key",
        ["help.spells.body"] = "On first launch Fova asks once whether your Flash sits on D or F; preset applies then always place Flash on that key. The [스펠 ⇄] button swaps your current spells instantly, and the key can be changed in Settings anytime.",
        ["help.wards.title"] = "Enemy control wards",
        ["help.wards.body"] = "Shows how many Control Wards each enemy is carrying (public scoreboard data). Wards inside this season's dedicated ward slot (V) are not exposed by the game and are not counted - the number reflects the regular inventory only.",
        ["help.search.title"] = "User search",
        ["help.search.body"] = "Look up match history and profiles by summoner name.",
        ["help.data.title"] = "Data sources & ads",
        ["help.data.body"] = "Uses Data Dragon, CommunityDragon, the local Live Client API, and the local LCU API only. Rune recommendations are aggregated ranked-match statistics; no per-player data ships. The single ad slot lives on the desktop home screen and is fully disabled during games.",

        // ── Settings: spells ─────────────────────────────────────────
        ["spell.1"] = "Cleanse",
        ["spell.3"] = "Exhaust",
        ["spell.4"] = "Flash",
        ["spell.6"] = "Ghost",
        ["spell.7"] = "Heal",
        ["spell.11"] = "Smite",
        ["spell.12"] = "Teleport",
        ["spell.14"] = "Ignite",
        ["spell.21"] = "Barrier",
        ["spell.swap"] = "Swap spell slots",
        ["spell.change"] = "Click to change spell",
        ["ward.title"] = "Control Wards",
        ["rec.title"] = "Recommended runes",
        ["rec.samples"] = "Games",
        ["rec.pickRate"] = "Pick",
        ["rec.winRate"] = "Win",
        // "sets", not "order" — aggregation is final-inventory based (no timeline collected).
        ["rec.items.title"] = "Core item sets ({0})",
        ["rec.items.title.single"] = "Recommended items ({0})",
        ["rec.items.boots"] = "Boots",
        ["settings.flashKey"] = "Flash key",
        ["settings.flashKey.desc"] = "Preset applies place Flash on this key",
        ["settings.flashKey.d"] = "Flash on D",
        ["settings.flashKey.f"] = "Flash on F",
        ["header.previewShow"] = "Preview Overlay",
        ["header.previewHide"] = "Hide Overlay",
        ["status.waiting"] = "Waiting for game",
        ["status.detected"] = "Game detected",

        ["home.title"] = "Dashboard",
        ["home.subtitle"] = "See your game state and saved settings at a glance.",
        ["home.gameState"] = "Game State",
        ["home.savedCombos"] = "Saved Combos",
        ["home.liveStats"] = "Live Stats",
        ["home.emptyState"] = "Live stats appear once you enter a game.",
        ["home.champion"] = "Champion",
        ["home.level"] = "Level",
        ["home.gold"] = "Gold",
        ["home.ad"] = "AD",
        ["home.ap"] = "AP",
        ["home.idle"] = "Idle",
        ["home.detected"] = "Game detected",

        ["search.title"] = "User Search",
        ["search.placeholder"] = "Enter summoner name",
        ["search.button"] = "Search",
        ["search.help"] = "Searches players in your current game by summoner name. External match history is coming later (requires an API key).",
        ["search.enterName"] = "Please enter a summoner name.",
        ["search.noGame"] = "No game in progress. ",
        ["search.resultCount"] = "'{0}': {1} result(s)",
        ["search.noMatch"] = "No player matching '{0}' in this game.",
        ["search.blue"] = "Blue Team",
        ["search.red"] = "Red Team",
        ["search.dead"] = "Dead",
        ["search.unknown"] = "(unknown)",

        ["settings.title"] = "Settings",
        ["settings.language"] = "Language",
        ["settings.languageDesc"] = "Choose the app display language. Changes apply immediately.",
        ["settings.korean"] = "한국어",
        ["settings.english"] = "English",

        ["combo.title"] = "Combo Settings",
        ["combo.build"] = "Build a Combo",
        ["combo.champion"] = "Champion",
        ["combo.championSelect"] = "Select champion",
        ["combo.championSearch"] = "Search champions",
        ["combo.palette"] = "Skill Palette",
        ["combo.paletteSkills"] = "Skills",
        ["combo.paletteMore"] = "Items",
        ["combo.paletteHint"] = "Select a champion to show its skills.",
        ["combo.itemSearch"] = "Search items to add",
        ["combo.itemHint"] = "Selected items are a HYPOTHETICAL build added on top of your current live stats for combo damage testing — not a replacement for your real in-game state.",
        ["combo.itemRemove"] = "Remove: {0}",
        ["combo.dragHint"] = "Drag a skill down to add it to the sequence.",
        ["combo.name"] = "Combo Name",
        ["combo.nameHint"] = "e.g. All-in combo",
        ["combo.hotkey"] = "Hotkey (optional)",
        ["combo.setHotkey"] = "Set Hotkey",
        ["combo.pressKey"] = "Press a key combination…",
        ["combo.hotkey2"] = "Second hotkey (optional)",
        ["combo.sequence"] = "Combo Sequence",
        ["combo.sequenceEmpty"] = "Drag skills here. Drag up to remove.",
        ["combo.clear"] = "Clear",
        ["combo.save"] = "Save",
        ["combo.saved"] = "Saved Combos",
        ["combo.emptySaved"] = "No saved combos yet.",
        ["combo.savedHint"] = "In game, press a bound hotkey to show that combo's damage on the overlay.",
        ["combo.noHotkey"] = "No hotkey",
        ["combo.hotkeyLabel"] = "Hotkey: {0}",
        ["combo.delete"] = "Delete",
        ["combo.noName"] = "(no name)",
        ["combo.initializing"] = "Data is still initializing. Please try again shortly.",
        ["combo.selectChampion"] = "Please select a champion.",
        ["combo.enterName"] = "Please enter a combo name.",
        ["combo.addSkill"] = "Add at least one skill to the sequence.",
        ["combo.saveFailed"] = "Save failed: {0}",
        ["combo.saveOk"] = "Saved combo '{0}'.",
        ["combo.skillLoadFailed"] = "Failed to load skills: {0}",

        // ── Defender target snapshot (virtual target) ────────────────
        ["combo.copyTargetStats"] = "Copy target stats",
        ["combo.useSnapshotTarget"] = "Use virtual target",
        ["combo.snapshotNone"] = "No captured target",
        ["combo.snapshotCaptured"] = "{0} · Armor {1} · MR {2} (captured {3} ago)",
        ["combo.snapshotCaptureOk"] = "Copied target stats.",
        ["combo.snapshotCaptureFailed"] = "No real target resolved — nothing was copied.",

        ["combo.addFx"] = "Attach bonus effect",
        ["combo.removeFx"] = "Remove bonus effect: {0}",
        ["combo.fxOnHit"] = "on-hit",
        ["combo.fxOnAbility"] = "on-ability",
        ["combo.fxSelf"] = "passive",
        ["combo.itemDetach"] = "Detach item: {0}",
        ["combo.hitDuration"] = "Set hit duration (max {0}s)",

        ["target.title"] = "Combo Target",
        ["target.auto"] = "Auto (same-lane enemy)",
        ["target.hint"] = "Auto targets the same-lane enemy. Pick a champion to compute against that specific target.",
    };
}
