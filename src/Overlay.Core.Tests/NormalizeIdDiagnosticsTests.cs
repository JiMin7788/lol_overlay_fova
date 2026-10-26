using Overlay.Core.ChampionDb;
using Overlay.Core.Jungle;
using Xunit;

namespace Overlay.Core.Tests;

/// <summary>
/// TODO:452 — the minimap trackers fall back to the raw name when <c>ResolveKoreanName</c> fails.
/// The fallback stays (an English id must pass through), but a name that is neither a known Korean
/// name nor a known id is now logged once per tracker instead of vanishing silently.
/// </summary>
public class NormalizeIdDiagnosticsTests : IDisposable
{
    private const string Unknown = "NotAChampion";

    public NormalizeIdDiagnosticsTests()
    {
        EventBus.EventBus.ResetForTests();
        ChampionLocalizationRepository.ResetForTests();
    }

    public void Dispose()
    {
        ChampionLocalizationRepository.ResetForTests();
        EventBus.EventBus.ResetForTests();
    }

    private static void InitLocalization() => ChampionLocalizationRepository.Initialize(
        new Dictionary<string, string> { ["MonkeyKing"] = "오공", ["Ahri"] = "아리" });

    [Fact]
    public void IsKnownChampionName_FollowsTheDefinition()
    {
        Assert.False(ChampionSummary.IsKnownChampionName(null!));
        Assert.False(ChampionSummary.IsKnownChampionName(""));
        Assert.True(ChampionSummary.IsKnownChampionName("아리"));        // static fallback table
        Assert.False(ChampionSummary.IsKnownChampionName("MonkeyKing")); // id, repo not initialized

        InitLocalization();
        Assert.True(ChampionSummary.IsKnownChampionName("오공"));
        Assert.True(ChampionSummary.IsKnownChampionName("MonkeyKing"));
        Assert.False(ChampionSummary.IsKnownChampionName(Unknown));
    }

    [Fact]
    public void Presence_UnknownName_LoggedOnce_ReturnUnchanged()
    {
        InitLocalization();
        var log = new List<string>();
        using var tracker = new JunglePresenceTracker(() => null) { Diagnostics = log.Add };

        Assert.Equal(Unknown, tracker.NormalizeId(Unknown));
        Assert.Equal(Unknown, tracker.NormalizeId(Unknown));

        var line = Assert.Single(log);
        Assert.Equal(
            $"presence: champion name '{Unknown}' not resolved to an id — kept as-is (locRepoInit=True)", line);
    }

    [Fact]
    public void Afterimage_UnknownName_LoggedOnce_ReturnUnchanged()
    {
        InitLocalization();
        var log = new List<string>();
        using var tracker = new EnemyAfterimageTracker { Diagnostics = log.Add };

        Assert.Equal(Unknown, tracker.NormalizeId(Unknown));
        Assert.Equal(Unknown, tracker.NormalizeId(Unknown));

        var line = Assert.Single(log);
        Assert.Equal(
            $"afterimage: champion name '{Unknown}' not resolved to an id — kept as-is (locRepoInit=True)", line);
    }

    [Fact]
    public void KnownKoreanName_AndKnownEnglishId_LogNothing()
    {
        InitLocalization();
        var log = new List<string>();
        using var presence = new JunglePresenceTracker(() => null) { Diagnostics = log.Add };
        using var afterimage = new EnemyAfterimageTracker { Diagnostics = log.Add };

        Assert.Equal("MonkeyKing", presence.NormalizeId("오공"));
        Assert.Equal("MonkeyKing", presence.NormalizeId("MonkeyKing"));
        Assert.Equal("MonkeyKing", afterimage.NormalizeId("오공"));
        Assert.Equal("MonkeyKing", afterimage.NormalizeId("MonkeyKing"));

        Assert.Empty(log);
    }

    [Fact]
    public void RepositoryNotInitialized_ReportsLocRepoInitFalse()
    {
        var log = new List<string>();
        using var presence = new JunglePresenceTracker(() => null) { Diagnostics = log.Add };
        using var afterimage = new EnemyAfterimageTracker { Diagnostics = log.Add };

        Assert.Equal(Unknown, presence.NormalizeId(Unknown));
        Assert.Equal(Unknown, afterimage.NormalizeId(Unknown));

        Assert.Equal(new[]
        {
            $"presence: champion name '{Unknown}' not resolved to an id — kept as-is (locRepoInit=False)",
            $"afterimage: champion name '{Unknown}' not resolved to an id — kept as-is (locRepoInit=False)",
        }, log);
    }

    [Fact]
    public void UnknownName_DifferentCase_LoggedOnceWithFirstSpelling_ReturnRaw()
    {
        InitLocalization();
        var log = new List<string>();
        using var presence = new JunglePresenceTracker(() => null) { Diagnostics = log.Add };

        Assert.Equal("Zzzunknown", presence.NormalizeId("Zzzunknown"));
        Assert.Equal("Zzzunknown", presence.NormalizeId("Zzzunknown"));
        Assert.Equal("ZZZUNKNOWN", presence.NormalizeId("ZZZUNKNOWN"));

        var line = Assert.Single(log);
        Assert.Equal(
            "presence: champion name 'Zzzunknown' not resolved to an id — kept as-is (locRepoInit=True)", line);
    }

    // QA lolF03 round 3 S1: '   ' and a tab were each logged as an unresolved champion name.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyWhitespaceAndNull_LogNothing_ReturnRaw(bool initRepo)
    {
        if (initRepo) InitLocalization();
        var log = new List<string>();
        using var presence = new JunglePresenceTracker(() => null) { Diagnostics = log.Add };
        using var afterimage = new EnemyAfterimageTracker { Diagnostics = log.Add };

        foreach (var name in new[] { "", "   ", "\t" })
        {
            Assert.Equal(name, presence.NormalizeId(name));
            Assert.Equal(name, afterimage.NormalizeId(name));
        }
        Assert.Null(presence.NormalizeId(null!));
        Assert.Null(afterimage.NormalizeId(null!));

        Assert.Empty(log);
    }

    [Fact]
    public void NullSink_DoesNotThrow_AndEmptyNamePassesThrough()
    {
        using var presence = new JunglePresenceTracker(() => null);
        using var afterimage = new EnemyAfterimageTracker();

        Assert.Equal(Unknown, presence.NormalizeId(Unknown));
        Assert.Equal(Unknown, afterimage.NormalizeId(Unknown));
        Assert.Equal("", presence.NormalizeId(""));
        Assert.Equal("", afterimage.NormalizeId(""));
    }
}
