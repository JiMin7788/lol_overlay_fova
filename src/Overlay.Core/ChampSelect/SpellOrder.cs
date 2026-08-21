namespace Overlay.Core.ChampSelect;

/// <summary>
/// Summoner-spell slot ordering for the user's Flash-key habit (2026-07-25 request): the LCU
/// my-selection contract maps <c>spell1Id</c> to the D key and <c>spell2Id</c> to F, so applying
/// a preset verbatim can land Flash on the wrong finger. Given the user's preference
/// (<c>champSelect.flashKey</c> = "D"/"F"), <see cref="Normalize"/> swaps the pair exactly when
/// Flash sits on the other slot; pages without Flash pass through untouched.
/// </summary>
public static class SpellOrder
{
    /// <summary>Flash's summoner-spell id — a stable Riot API contract value (SummonerFlash has
    /// been id 4 across every patch; this is an API identifier, not patch-tuned game data).</summary>
    public const int FlashId = 4;

    /// <summary>Returns the pair ordered so Flash lands on the preferred key
    /// (<paramref name="flashOnF"/>: true = F/spell2, false = D/spell1). Pairs without Flash —
    /// or degenerate double-Flash input — return unchanged.</summary>
    public static (int Spell1, int Spell2) Normalize(int spell1, int spell2, bool flashOnF)
    {
        bool onD = spell1 == FlashId;
        bool onF = spell2 == FlashId;
        if (onD == onF) return (spell1, spell2);     // no Flash (or both) → nothing to place
        return onF == flashOnF ? (spell1, spell2) : (spell2, spell1);
    }
}
