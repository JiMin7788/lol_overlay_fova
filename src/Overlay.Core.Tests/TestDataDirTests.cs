using System.Security.Cryptography;
using System.Text;

namespace Overlay.Core.Tests;

/// <summary>
/// Test mode (<see cref="TestDataDir"/>): which FOVA_USER_DATA_DIR values enter it, and the
/// per-dir suffix that keeps a UI-test copy's single-instance names apart from the user's Fova.
/// </summary>
public class TestDataDirTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_Blank_IsNull(string? raw)
        => Assert.Null(TestDataDir.Resolve(raw));

    [Fact]
    public void Resolve_RelativePath_IsFullPath()
        => Assert.Equal(Path.GetFullPath("fova-test-data"), TestDataDir.Resolve("fova-test-data"));

    [Fact]
    public void Resolve_TrimsSurroundingSpaces()
        => Assert.Equal(Path.GetFullPath(@"C:\FovaTest\Data"), TestDataDir.Resolve(@"  C:\FovaTest\Data  "));

    [Fact]
    public void InstanceSuffix_IsDotAndEightLowercaseHex()
    {
        string suffix = TestDataDir.InstanceSuffix(@"C:\FovaTest\Data");
        Assert.Equal(9, suffix.Length);
        Assert.StartsWith(".", suffix);
        Assert.All(suffix.Substring(1), c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void InstanceSuffix_IgnoresCaseAndTrailingSeparator()
    {
        string expected = TestDataDir.InstanceSuffix(@"C:\FovaTest\Data");
        Assert.Equal(expected, TestDataDir.InstanceSuffix(@"c:\fovatest\data"));
        Assert.Equal(expected, TestDataDir.InstanceSuffix(@"C:\FovaTest\Data\"));
        Assert.Equal(expected, TestDataDir.InstanceSuffix(@"C:\FovaTest\Data/"));
    }

    [Fact]
    public void InstanceSuffix_DiffersForAnotherDir()
        => Assert.NotEqual(TestDataDir.InstanceSuffix(@"C:\FovaTest\Data"), TestDataDir.InstanceSuffix(@"C:\FovaTest\Other"));

    [Fact]
    public void InstanceSuffix_PinnedValue()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(@"C:\FOVATEST\DATA"));
        string expected = "." + string.Concat(hash.Take(4).Select(b => b.ToString("x2")));
        Assert.Equal(expected, TestDataDir.InstanceSuffix(@"C:\FovaTest\Data\"));
    }

    [Fact]
    public void AssetCacheDir_InstalledTestCopy_IsUnderTestDir()
        => Assert.Equal(@"C:\FovaTest\Data\cache",
            TestDataDir.AssetCacheDir(true, @"C:\FovaTest\Data", @"C:\Local\Fova", @"C:\App"));

    [Fact]
    public void AssetCacheDir_InstalledNoTestDir_IsUnderInstalledDataDir()
        => Assert.Equal(@"C:\Local\Fova\cache",
            TestDataDir.AssetCacheDir(true, null, @"C:\Local\Fova", @"C:\App"));

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\FovaTest\Data")]
    public void AssetCacheDir_NotInstalled_IsDataNextToExe(string? testDir)
        => Assert.Equal(@"C:\App\data",
            TestDataDir.AssetCacheDir(false, testDir, @"C:\Local\Fova", @"C:\App"));
}
