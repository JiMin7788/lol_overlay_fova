using Overlay.Core.Combo;

namespace Overlay.Core.ChampSelect;

/// <summary>
/// (M41) The enemy most likely to be in my lane, for the kill timing card. Champ select does not
/// expose enemy positions, so this compares roles: mine is the LCU's assignedPosition when the queue
/// has one (draft/ranked), else my champion's most-played role; each enemy's is its champion's
/// most-played role (<see cref="SkillOrders.RoleFor"/>). A guess the user can override by clicking
/// another enemy portrait.
/// </summary>
public static class LaneOpponent
{
    /// <summary>My role in the skill-order data's vocabulary ("top", "jungle", "mid", "adc",
    /// "support"): the LCU position mapped, else my champion's role. Null when neither is known.</summary>
    public static string? MyRole(string assignedPosition, string myChampionId)
        => assignedPosition.ToLowerInvariant() switch
        {
            "top" => "top",
            "jungle" => "jungle",
            "middle" or "mid" => "mid",
            "bottom" => "adc",
            "utility" => "support",
            _ => SkillOrders.RoleFor(myChampionId),
        };

    /// <summary>The first enemy whose champion's role matches <paramref name="myRole"/>, else the
    /// first enemy; null when there is none.</summary>
    public static string? Guess(string? myRole, IReadOnlyList<string> enemyChampionIds)
    {
        if (enemyChampionIds.Count == 0) return null;
        if (myRole is not null)
            foreach (var id in enemyChampionIds)
                if (string.Equals(SkillOrders.RoleFor(id), myRole, StringComparison.OrdinalIgnoreCase))
                    return id;
        return enemyChampionIds[0];
    }
}
