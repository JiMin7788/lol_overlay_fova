using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 P1 calibration layer 0: <see cref="GameCfgReader"/> is a pure, tolerant INI parse of
/// League's <c>game.cfg [HUD]</c> minimap keys. These tests pin the parse mechanics — they do
/// NOT assert the numeric MinimapScale→pixel relationship (that needs a live client, out of this
/// sandbox's reach — see the M31 P1 entry in CLAUDE_CODE_TODO.md).
/// </summary>
public class GameCfgReaderTests
{
    private const string Sample =
        "[General]\n" +
        "WindowMode=0\n" +
        "[HUD]\n" +
        "MinimapScale=0.6\n" +
        "FlipMiniMap=1\n" +
        "ShowNeutralCampTimers=1\n";

    [Fact]
    public void Parse_ReadsHudMinimapScaleAndFlip()
    {
        var cfg = GameCfgReader.Parse(Sample);

        Assert.Equal(0.6, cfg.MinimapScale!.Value, precision: 6);
        Assert.True(cfg.FlipMiniMap);
        Assert.False(cfg.IsEmpty);
    }

    [Fact]
    public void Parse_IsCaseInsensitive_ForSectionAndKeys()
    {
        var cfg = GameCfgReader.Parse("[hud]\nminimapscale=0.42\nflipminimap=0\n");

        Assert.Equal(0.42, cfg.MinimapScale!.Value, precision: 6);
        Assert.False(cfg.FlipMiniMap);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1.0000", true)]  // some clients persist floats
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("0.0", false)]
    public void Parse_FlipMiniMap_AcceptsIntBoolAndFloatForms(string value, bool expected)
    {
        var cfg = GameCfgReader.Parse($"[HUD]\nFlipMiniMap={value}\n");
        Assert.Equal(expected, cfg.FlipMiniMap);
    }

    [Fact]
    public void Parse_MalformedScale_IsNull_NotAThrow()
    {
        var cfg = GameCfgReader.Parse("[HUD]\nMinimapScale=abc\n");
        Assert.Null(cfg.MinimapScale);
    }

    [Fact]
    public void Parse_KeysOutsideHudSection_AreIgnored()
    {
        // A MinimapScale in another section must NOT be picked up as the HUD value.
        var cfg = GameCfgReader.Parse("[General]\nMinimapScale=9\n[HUD]\nFlipMiniMap=1\n");

        Assert.Null(cfg.MinimapScale);
        Assert.True(cfg.FlipMiniMap);
    }

    [Fact]
    public void Parse_LastOccurrenceWins()
    {
        var cfg = GameCfgReader.Parse("[HUD]\nMinimapScale=0.3\nMinimapScale=0.8\n");
        Assert.Equal(0.8, cfg.MinimapScale!.Value, precision: 6);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var cfg = GameCfgReader.Parse("\n; a comment\n[HUD]\n# hash comment\nMinimapScale=0.5\n");
        Assert.Equal(0.5, cfg.MinimapScale!.Value, precision: 6);
    }

    [Fact]
    public void Parse_NoHudSection_YieldsEmpty()
    {
        var cfg = GameCfgReader.Parse("[General]\nWindowMode=1\n");
        Assert.True(cfg.IsEmpty);
        Assert.Null(cfg.MinimapScale);
        Assert.Null(cfg.FlipMiniMap);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_YieldsEmpty(string? text)
    {
        Assert.True(GameCfgReader.Parse(text).IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Read_BlankOrMissingPath_YieldsEmpty_NoThrow(string? path)
    {
        Assert.True(GameCfgReader.Read(path).IsEmpty);
    }

    [Fact]
    public void Read_NonexistentFile_YieldsEmpty_NoThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-game-" + Guid.NewGuid() + ".cfg");
        Assert.True(GameCfgReader.Read(missing).IsEmpty);
    }

    [Fact]
    public void Read_RealFile_RoundTripsThroughParse()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "game-" + Guid.NewGuid() + ".cfg");
        File.WriteAllText(tmp, Sample);
        try
        {
            var cfg = GameCfgReader.Read(tmp);
            Assert.Equal(0.6, cfg.MinimapScale!.Value, precision: 6);
            Assert.True(cfg.FlipMiniMap);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
