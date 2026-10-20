namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 15) The duration-scaled zone the M24 "적중시간" tests were built on: Malzahar's pre-rework W
/// "Null Zone", a literal 6% max-health-per-second escapable zone for up to 5 seconds. Real Malzahar W has
/// been Void Swarm since 2015 (Null Zone is part of his R), so the data file no longer carries it; the
/// tests load this fixture through <c>SkillDamageDb.LoadForTests</c> instead, and keep asserting the
/// mechanism - literal fields, no BIN needed - exactly as before.
/// </summary>
internal static class NullZoneFixture
{
    public const string Json =
        "{ \"W\": { \"hits\": [ { \"type\": \"Magic\", \"perSecondHpPercent\": 0.06, \"maxDurationSeconds\": 5, " +
        "\"count\": 1, \"tags\": [\"Zone\"] } ] } }";
}
