namespace Overlay.Core.ChampionDb;

/// <summary>
/// The M11 spec (Internal Logic step 4) lists 8 runes, by Korean name, whose real-time
/// effect state cannot be confirmed via any public API and must therefore be marked
/// apiTrackable:false / effectFormula:null so M06 Rune Engine falls back to a manual
/// checkbox. Data Dragon's runesReforged.json only carries English names, so the
/// Korean names were matched to Riot's official ko_KR localization of the same file
/// (see M11 Agent Report "Notes for Reviewer" for the verification method and the
/// one non-obvious mapping: 죽음불꽃손아귀 -&gt; Deathfire Touch).
/// </summary>
public static class RuneApiTrackability
{
    /// <summary>Data Dragon numeric rune ids for the 8 spec-listed non-trackable runes.</summary>
    public static readonly IReadOnlySet<int> NonTrackableRuneIds = new HashSet<int>
    {
        8126, // Cheap Shot            (비열한 한방)      — Domination
        8143, // Sudden Impact         (돌발일격)         — Domination
        8992, // Deathfire Touch       (죽음불꽃손아귀)   — Sorcery
        8237, // Scorch                (주문작열)         — Sorcery
        8401, // Shield Bash           (보호막강타)       — Resolve
        8229, // Arcane Comet          (신비로운 유성)    — Sorcery
        8437, // Grasp of the Undying  (착취의 손아귀)    — Resolve
        8369, // First Strike          (선제공격)         — Inspiration
    };
}
