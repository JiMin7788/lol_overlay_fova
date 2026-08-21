namespace Overlay.Core.Recall;

/// <summary>M08 Data Model: how a <see cref="ReturnETA"/> was derived.</summary>
public enum EtaBasis
{
    /// <summary>Only the respawn timer is modelled (no travel component — e.g. the champion
    /// is at/near the lane or no distance was supplied).</summary>
    RespawnTimerOnly,

    /// <summary>Remaining respawn time PLUS the fountain→lane travel estimate.</summary>
    RespawnPlusTravel,
}

/// <summary>
/// M08 Data Model. An estimated return-to-lane time. This is an <b>estimate</b> computed from
/// confirmed inputs (respawn time, fixed preset distance, supplied average move speed) — it is
/// NOT a live position track of an out-of-vision champion (spec Policy Checklist). The estimate
/// nature is baked into <see cref="Message"/> so a consumer never renders it as confirmed info.
/// </summary>
public sealed record ReturnETA(string ChampionName, double EstimatedArrivalSeconds, EtaBasis Basis)
{
    /// <summary>Display text carrying an explicit "estimate" marker (spec Internal Logic #3 /
    /// Reviewer Checklist #1). Unlike the exact Death Timer, an ETA must never be shown as
    /// confirmed.</summary>
    public string Message => $"~{EstimatedArrivalSeconds:0.#}s (estimate)";
}
