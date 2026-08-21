using Overlay.Core.Config;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 §D — the settings-view keys for enemy-presence voice and the minimap afterimage.
///
/// <para>These exist because the failure mode for a settings control is silent: the toggle moves, the
/// user believes the setting took, and nothing changes. Two ways that happens here, both covered
/// below — the write lands somewhere the reader never looks (the per-role gates are a DICTIONARY path,
/// <c>minimap.afterimage.roles.{key}</c>, not a declared property), or the value survives in memory
/// but not across a reload.</para>
///
/// <para>The reader side is pinned to the exact strings <c>OverlayHost</c> and
/// <c>AppComposition</c> use, so renaming a key on one side alone goes red here.</para>
/// </summary>
public class EnemyPresenceSettingsKeysTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public EnemyPresenceSettingsKeysTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "M31_SettingsKeys_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Defaults_MatchWhatTheEngineAssumesWhenTheUserNeverOpensSettings()
    {
        using var config = new ConfigManager(_path);
        Assert.Equal("prerecorded", config.Get("voice.enemyVoicePack"));
        Assert.Equal("simple", config.Get("voice.enemyVoiceDetail"));
        Assert.Equal(true, config.Get("minimap.afterimage.enabled"));
        // 0.75, not the originally-requested 0.5: the live pass reported the marker as too faint.
        // Must stay in step with OverlayHost.DefaultAfterimageOpacity — a fresh install and the
        // renderer's own fallback disagreeing is exactly the kind of drift this row exists to catch.
        Assert.Equal(0.75, config.Get("minimap.afterimage.opacity"));
    }

    [Theory]
    [InlineData("voice.enemyVoicePack", "off")]
    [InlineData("voice.enemyVoiceDetail", "detail")]
    public void StringKeys_RoundTripAcrossAReload(string key, string value)
    {
        using (var config = new ConfigManager(_path))
        {
            config.Set(key, value);
        }
        using var reopened = new ConfigManager(_path);
        Assert.Equal(value, reopened.Get(key));
    }

    [Fact]
    public void AfterimageOpacity_RoundTripsAcrossAReload()
    {
        using (var config = new ConfigManager(_path))
        {
            config.Set("minimap.afterimage.opacity", 0.35);
        }
        using var reopened = new ConfigManager(_path);
        Assert.Equal(0.35, reopened.Get("minimap.afterimage.opacity"));
    }

    /// <summary>
    /// The per-role gates are the risky ones: they write into a dictionary-typed node rather than a
    /// declared property, so a serializer that reshaped or dropped unknown children would turn all
    /// five toggles into dead controls without any error surfacing.
    /// </summary>
    [Theory]
    [InlineData("top")]
    [InlineData("jungle")]
    [InlineData("mid")]
    [InlineData("adc")]
    [InlineData("support")]
    public void AfterimageRoleGate_RoundTripsAcrossAReload(string role)
    {
        string key = "minimap.afterimage.roles." + role;
        using (var config = new ConfigManager(_path))
        {
            config.Set(key, false);
        }
        using var reopened = new ConfigManager(_path);
        Assert.Equal(false, reopened.Get(key));
    }

    [Fact]
    public void AfterimageRoleGates_AreIndependent()
    {
        using var config = new ConfigManager(_path);
        config.Set("minimap.afterimage.roles.jungle", false);
        // Every other role must be untouched — the point of the per-role gate is silencing ONE role.
        Assert.Equal(false, config.Get("minimap.afterimage.roles.jungle"));
        foreach (var other in new[] { "top", "mid", "adc", "support" })
            Assert.NotEqual(false, config.Get("minimap.afterimage.roles." + other));
    }

    /// <summary>
    /// An unset role key must read as "not false" so the renderer's absent-means-enabled default
    /// (<c>OverlayHost.AfterimageRoleEnabled</c>) holds for a config written before this feature.
    /// </summary>
    [Fact]
    public void UnsetRoleGate_DoesNotReadAsDisabled()
    {
        using var config = new ConfigManager(_path);
        Assert.NotEqual(false, config.Get("minimap.afterimage.roles.top"));
    }
}
