namespace Overlay.Core.Recall;

/// <summary>
/// M08 Data Model. A champion death observed via M01's <c>GAME.CHAMPION_DIED</c>.
/// <see cref="RespawnTimer"/> is the RAW Live Client <c>respawnTimer</c> value in seconds —
/// carried through with NO correction or rounding so the Death Timer countdown matches the
/// exact in-game value (Reviewer Checklist #2 / Acceptance 0.2s).
/// </summary>
public sealed record DeathEvent(string ChampionName, double RespawnTimer, long DeathTimestamp);
