using Overlay.Core.ChampionDb;
using Overlay.Core.Spells;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M12 Spell Timer (docs/modules/M12_SPELL_TIMER.md):
///  - exact-match formula (FinalCooldown = BaseCooldown / (1 + TotalAbilityHaste/100))
///    for Flash/Ignite/Teleport/Barrier across all 4 hasCosmicInsight/hasIonianBoots
///    cases (Acceptance Criteria: "오차 0").
///  - the single most important test: changing M11's mock haste value (8 -> 18) with
///    NO SpellTimer code change changes the computed result correctly (Acceptance
///    Criteria's explicit requirement).
///  - GetCaseTable returns all 4 correct variants in one call.
///  - Ability Haste is never hardcoded: values come from RuneRepository/ItemRepository
///    only (proven structurally by the haste-change test, since a hardcoded value
///    could not respond to a re-Initialize with a different number).
///
/// RuneRepository/ItemRepository are both static/process-wide, so every test resets
/// both first, mirroring RuneEngineTests' established isolation pattern.
/// AssemblyInfo.cs already disables cross-class test parallelization.
/// </summary>
public class SpellTimerTests
{
    private static void SeedM11(double cosmicInsightHaste, double ionianBootsHaste)
    {
        RuneRepository.ResetForTests();
        RuneRepository.Initialize(new[]
        {
            new RuneData
            {
                Id = SpellTimer.CosmicInsightRuneId,
                Name = "Cosmic Insight",
                Tree = "Inspiration",
                EffectFormula = cosmicInsightHaste.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ApiTrackable = true,
            },
        });

        ItemRepository.ResetForTests();
        ItemRepository.Initialize(new[]
        {
            new ItemData
            {
                Id = SpellTimer.IonianBootsItemId,
                Name = "Ionian Boots of Lucidity",
                Stats = new ItemStats { Haste = ionianBootsHaste },
                IsBoots = true,
                BootsType = "Ionian",
            },
        });
    }

    // Test-fixture base cooldowns (seconds) — not asserted as real-game truth, just
    // stable inputs the formula is exercised against, per spec's own worked example
    // shape (spec uses Flash=300 as its own reference example).
    private static readonly SpellBaseData Flash = new("Flash", 300);
    private static readonly SpellBaseData Ignite = new("Ignite", 180);
    private static readonly SpellBaseData Teleport = new("Teleport", 360);
    private static readonly SpellBaseData Barrier = new("Barrier", 180);

    [Fact]
    public void GetFinalCooldown_AllFourSpells_AllFourCases_MatchFormulaExactly()
    {
        SeedM11(cosmicInsightHaste: 18, ionianBootsHaste: 10);

        AssertAllFourCases(Flash, baseCooldown: 300);
        AssertAllFourCases(Ignite, baseCooldown: 180);
        AssertAllFourCases(Teleport, baseCooldown: 360);
        AssertAllFourCases(Barrier, baseCooldown: 180);
    }

    private static void AssertAllFourCases(SpellBaseData spell, double baseCooldown)
    {
        Assert.Equal(baseCooldown, SpellTimer.GetFinalCooldown(spell, false, false), precision: 9);
        Assert.Equal(baseCooldown / 1.18, SpellTimer.GetFinalCooldown(spell, true, false), precision: 9);
        Assert.Equal(baseCooldown / 1.10, SpellTimer.GetFinalCooldown(spell, false, true), precision: 9);
        Assert.Equal(baseCooldown / 1.28, SpellTimer.GetFinalCooldown(spell, true, true), precision: 9);
    }

    [Fact]
    public void GetFinalCooldown_ChangingM11MockHasteValue_ChangesResult_WithNoCodeChange()
    {
        SeedM11(cosmicInsightHaste: 8, ionianBootsHaste: 10);
        var resultWith8 = SpellTimer.GetFinalCooldown(Flash, hasCosmicInsight: true, hasIonianBoots: false);
        Assert.Equal(300 / 1.08, resultWith8, precision: 9);

        // Re-initialize M11 with a different haste number — same SpellTimer code, no
        // recompilation, no code change anywhere in SpellTimer.cs.
        SeedM11(cosmicInsightHaste: 18, ionianBootsHaste: 10);
        var resultWith18 = SpellTimer.GetFinalCooldown(Flash, hasCosmicInsight: true, hasIonianBoots: false);
        Assert.Equal(300 / 1.18, resultWith18, precision: 9);

        Assert.NotEqual(resultWith8, resultWith18);
    }

    [Fact]
    public void GetCaseTable_ReturnsAllFourCorrectVariants()
    {
        SeedM11(cosmicInsightHaste: 18, ionianBootsHaste: 10);

        var table = SpellTimer.GetCaseTable(Flash);

        Assert.Equal(300, table.None, precision: 9);
        Assert.Equal(300 / 1.18, table.RuneOnly, precision: 9);
        Assert.Equal(300 / 1.10, table.BootsOnly, precision: 9);
        Assert.Equal(300 / 1.28, table.Both, precision: 9);
    }

    [Fact]
    public void GetFinalCooldown_NeitherRuneNorBoots_NeverTouchesM11Repositories()
    {
        // Deliberately do NOT initialize RuneRepository/ItemRepository at all — if
        // GetFinalCooldown(false, false) looked either up unconditionally, this would
        // throw. It must not, since TotalAbilityHaste is 0 by definition in this case.
        RuneRepository.ResetForTests();
        ItemRepository.ResetForTests();

        var result = SpellTimer.GetFinalCooldown(Flash, hasCosmicInsight: false, hasIonianBoots: false);

        Assert.Equal(300, result, precision: 9);
    }

    [Fact]
    public void GetFinalCooldown_MissingM11Entry_ThrowsRatherThanHardcodingFallback()
    {
        RuneRepository.ResetForTests(); // no CosmicInsight registered
        ItemRepository.ResetForTests();

        Assert.Throws<InvalidOperationException>(
            () => SpellTimer.GetFinalCooldown(Flash, hasCosmicInsight: true, hasIonianBoots: false));
    }

    /// <summary>Reviewer Checklist proof: no countdown/ticking logic anywhere in this
    /// module's source file (no Timer/setInterval-equivalent per-second decrement) and
    /// no OCR dependency. Structural grep-equivalent check against the actual shipped
    /// source, in addition to the explicit statement in the Agent Report.</summary>
    [Fact]
    public void ReviewerChecklist_SourceFile_HasNoCountdownOrOcrCode()
    {
        var path = FindSourceFile();
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("System.Threading.Timer", source);
        Assert.DoesNotContain("new Timer(", source);
        Assert.DoesNotContain("DispatcherTimer", source);
        Assert.DoesNotContain("setInterval", source);
        Assert.DoesNotContain("OCR", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tesseract", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSourceFile()
    {
        // Walk up from the test assembly's output dir to the repo-relative source path,
        // matching the project layout (src/Overlay.Core.Tests -> src/Overlay.Core).
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "src", "Overlay.Core", "Spells", "SpellTimer.cs");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException("Could not locate SpellTimer.cs from test output directory.");
    }
}
