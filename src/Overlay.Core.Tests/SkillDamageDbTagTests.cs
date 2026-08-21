using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// Proves the M21 tag-taxonomy schema addition to <see cref="SkillDamageDb"/>:
///  (a) a pre-existing curated file with NO "tags" key anywhere still parses identically
///      (full backward-compatibility);
///  (b) a per-hit "tags" array round-trips through the loader;
///  (c) a per-slot "tags" array round-trips through <see cref="SkillDamageDb.GetSlotTags"/>;
///  (d) the Load() bug fix: a slot with ONLY tags and no hits/bonusEffects (a pure-utility
///      shred-only skill with no direct damage hit) is no longer silently dropped;
///  (e) a handful of the 67 retrofitted champion files' real, hand-checked new tags parse
///      correctly (spot-check, not exhaustive).
///
/// (a)-(d) use a synthetic fixture file written into the real (deployed) skill_damage
/// directory under a unique throwaway champion id — same pattern <see cref="AllChampionSkillDataTests"/>
/// already uses for that directory — cleaned up in Dispose.
/// </summary>
public class SkillDamageDbTagTests : IDisposable
{
    private static string SkillDamageDir => Path.Combine(AppContext.BaseDirectory, "data", "skill_damage");

    private readonly List<string> _writtenFixtures = new();

    public SkillDamageDbTagTests()
    {
        SkillDamageDb.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var path in _writtenFixtures)
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
        SkillDamageDb.ResetForTests();
    }

    private string WriteFixture(string championId, string json)
    {
        Directory.CreateDirectory(SkillDamageDir);
        var path = Path.Combine(SkillDamageDir, championId + ".json");
        File.WriteAllText(path, json);
        _writtenFixtures.Add(path);
        SkillDamageDb.ResetForTests(); // ensure a fresh load picks up this fixture, not a cached miss
        return path;
    }

    // ── (a) backward-compat: a file with no "tags" key anywhere parses identically ─────────────

    [Fact]
    public void FileWithNoTagsKey_ParsesIdentically_TagsFieldsNullOrEmpty()
    {
        const string championId = "ZZTagFixtureNoTags";
        WriteFixture(championId, """
        {
          "Q": { "hits": [ { "type": "Physical", "calc": "QDamage", "count": 1 } ] }
        }
        """);

        var hits = SkillDamageDb.GetHits(championId, "Q");
        Assert.NotNull(hits);
        var hit = Assert.Single(hits!);
        Assert.Null(hit.Tags); // per-hit Tags omitted -> null, not an empty array

        var slotTags = SkillDamageDb.GetSlotTags(championId, "Q");
        Assert.NotNull(slotTags);
        Assert.Empty(slotTags); // per-slot GetSlotTags always returns non-null, empty when uncurated
    }

    // ── (b) per-hit tags round-trip ──────────────────────────────────────────────────────────

    [Fact]
    public void PerHitTags_ParseCorrectly()
    {
        const string championId = "ZZTagFixturePerHit";
        WriteFixture(championId, """
        {
          "R": { "hits": [ { "type": "True", "hpPercentDataValue": "ExecuteDamage", "hpBasis": "Missing", "count": 1, "tags": ["PercentMissingHp", "Execute"] } ] }
        }
        """);

        var hits = SkillDamageDb.GetHits(championId, "R");
        var hit = Assert.Single(hits!);
        Assert.NotNull(hit.Tags);
        Assert.Equal(new[] { "PercentMissingHp", "Execute" }, hit.Tags);
    }

    // ── (c) per-slot tags round-trip via GetSlotTags ─────────────────────────────────────────

    [Fact]
    public void PerSlotTags_ParseCorrectly_ViaGetSlotTags()
    {
        const string championId = "ZZTagFixturePerSlot";
        WriteFixture(championId, """
        {
          "R": { "tags": ["BurstCeiling"], "hits": [ { "type": "Magic", "calc": "RDamage", "count": 1 } ] }
        }
        """);

        var tags = SkillDamageDb.GetSlotTags(championId, "R");
        Assert.Equal(new[] { "BurstCeiling" }, tags);

        // A slot never curated at all still returns empty, never null/throws.
        Assert.Empty(SkillDamageDb.GetSlotTags(championId, "Q"));
        Assert.Empty(SkillDamageDb.GetSlotTags("ZZNoSuchChampion", "R"));
    }

    // ── (d) THE BUG FIX: a tags-only slot (no hits, no bonusEffects) is retained ────────────────

    [Fact]
    public void TagsOnlySlot_NoHitsNoBonusEffects_IsRetained_NotSilentlyDropped()
    {
        const string championId = "ZZTagFixtureTagsOnlySlot";
        WriteFixture(championId, """
        {
          "E": { "tags": ["ArmorShred"] }
        }
        """);

        // Before the fix, Load()'s keep condition was `hits.Length > 0 || bonus.Length > 0`,
        // which would drop this slot entirely -> GetSlotTags would wrongly return empty.
        var tags = SkillDamageDb.GetSlotTags(championId, "E");
        Assert.Equal(new[] { "ArmorShred" }, tags);

        // The slot legitimately has no hits/bonus effects — GetHits/GetBonusEffects correctly
        // still report "nothing here", but the slot object itself was not dropped (tags survive).
        Assert.Null(SkillDamageDb.GetHits(championId, "E"));
        Assert.Null(SkillDamageDb.GetBonusEffects(championId, "E"));
    }

    // A slot with genuinely nothing (no hits, no bonusEffects, no tags) is still metadata-only
    // and correctly ignored (unchanged pre-existing behavior).
    [Fact]
    public void EmptySlotObject_NoHitsNoBonusNoTags_IsIgnored()
    {
        const string championId = "ZZTagFixtureEmptySlot";
        WriteFixture(championId, """
        {
          "P": {}
        }
        """);

        Assert.Null(SkillDamageDb.GetHits(championId, "P"));
        Assert.Empty(SkillDamageDb.GetSlotTags(championId, "P"));
    }

    // ── (e) spot-check real retrofitted files (Task 2) ──────────────────────────────────────────

    [Fact]
    public void Garen_R_RetrofittedTags_ParseCorrectly()
    {
        SkillDamageDb.ResetForTests();
        var slotTags = SkillDamageDb.GetSlotTags("Garen", "R");
        Assert.Contains("BurstCeiling", slotTags);

        var hits = SkillDamageDb.GetHits("Garen", "R");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        // (GOLDEN #3 round 2 ordering fix) missing-HP hit now resolves FIRST, flat hit SECOND —
        // see Garen.json's _noteR2 for why (both hits fire within one simultaneous R cast, so the
        // missing-HP hit must read the target's PRE-CAST HP, not HP already reduced by the flat hit).
        Assert.NotNull(hits[0].Tags);
        Assert.Contains("PercentMissingHp", hits[0].Tags!);
        Assert.Null(hits[1].Tags); // flat BaseDataValue hit — not itself a %HP hit, no tag
    }

    [Fact]
    public void Vayne_W_RetrofittedTag_PercentMaxHp_ParsesCorrectly()
    {
        SkillDamageDb.ResetForTests();
        var hits = SkillDamageDb.GetHits("Vayne", "W");
        var hit = Assert.Single(hits!);
        Assert.Equal(HpBasis.Max, hit.HpBasis);
        Assert.NotNull(hit.Tags);
        Assert.Contains("PercentMaxHp", hit.Tags!);
    }

    [Fact]
    public void Malzahar_W_RetrofittedTag_Zone_ParsesCorrectly()
    {
        SkillDamageDb.ResetForTests();
        var hits = SkillDamageDb.GetHits("Malzahar", "W");
        var hit = Assert.Single(hits!);
        Assert.True(hit.IsDurationScaled);
        Assert.NotNull(hit.Tags);
        Assert.Contains("Zone", hit.Tags!);

        // R is Malzahar's burst-ceiling slot.
        Assert.Contains("BurstCeiling", SkillDamageDb.GetSlotTags("Malzahar", "R"));
    }
}
