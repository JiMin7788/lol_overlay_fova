namespace Overlay.Core.Tests;

/// <summary>(lolf01-t10) The version appears once in the composed version text.</summary>
public class VersionTextTests
{
    [Fact]
    public void ReleaseStampAlreadyLeadsWithVersion_ReturnsStampOnce()
    {
        Assert.Equal("0.4.1 abc123def 2026-10-23 10:00:00",
            VersionText.Compose("0.4.1", "0.4.1 abc123def 2026-10-23 10:00:00"));
    }

    [Fact]
    public void StampWithoutVersion_IsParenthesizedAfterRelease()
    {
        Assert.Equal("0.4.1 (abc123def 2026-10-23 10:00:00)",
            VersionText.Compose("0.4.1", "abc123def 2026-10-23 10:00:00"));
    }

    [Fact]
    public void NoRelease_ReturnsInformationalUnchanged()
    {
        const string stamp = "abc123def+dirty 2026-10-23 10:00:00";
        Assert.Equal(stamp, VersionText.Compose(null, stamp));
        Assert.Equal(stamp, VersionText.Compose("", stamp));
    }

    [Fact]
    public void LongerVersion_IsNotTakenAsThePrefix()
    {
        Assert.Equal("0.4.1 (0.4.10 x)", VersionText.Compose("0.4.1", "0.4.10 x"));
    }
}
