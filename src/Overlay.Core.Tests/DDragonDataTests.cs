using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

/// <summary>Which downloaded Data Dragon version folder the app runs on.</summary>
public class DDragonDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fova-ddragon-" + Guid.NewGuid().ToString("N"));
    private readonly string _root2 = Path.Combine(Path.GetTempPath(), "fova-ddragon2-" + Guid.NewGuid().ToString("N"));

    private static void Version(string root, string version, bool complete = true)
    {
        var dir = Path.Combine(root, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "item.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "runesReforged.json"), "[]");
        if (complete) File.WriteAllText(Path.Combine(dir, "champion.json"), "{}");
    }

    [Fact]
    public void NewestIsNumeric_NotOrdinal()
    {
        Version(_root, "16.9.1");
        Version(_root, "16.13.1");
        Assert.Equal("16.13.1", Path.GetFileName(DDragonData.FindNewest(new[] { _root })));
    }

    [Fact]
    public void IncompleteFolder_IsSkipped()
    {
        Version(_root, "16.13.1");
        Version(_root, "16.20.1", complete: false); // download still running (champion.json comes last)
        Assert.Equal("16.13.1", Path.GetFileName(DDragonData.FindNewest(new[] { _root })));
    }

    [Fact]
    public void NewestAcrossRoots_AndNonVersionFoldersIgnored()
    {
        Version(_root, "16.13.1");
        Version(_root2, "16.20.1");
        Directory.CreateDirectory(Path.Combine(_root, "img"));
        Assert.Equal("16.20.1", Path.GetFileName(DDragonData.FindNewest(new[] { _root, _root2 })));
    }

    [Fact]
    public void NothingComplete_ReturnsNull()
        => Assert.Null(DDragonData.FindNewest(new[] { _root }));

    public void Dispose()
    {
        foreach (var r in new[] { _root, _root2 })
            try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch { }
    }
}
