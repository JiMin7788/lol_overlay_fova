using System.Text.Json;
using System.Text.Json.Nodes;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Client.UiTests;

/// <summary>
/// The engine numbers the editor should show, computed in-process on the app copy's own data.
/// Initialisation mirrors <c>Program.Main</c> + <c>AppComposition.InitializeAsync</c>
/// (RECURRING_TRAPS 2.4: the app's path, not a poorer fixture), and <see cref="Preview"/> is the
/// synchronous <c>ComboRunner.ComputePreview</c> (RECURRING_TRAPS 2.3: no event bus). It runs the same
/// engine as the app, so it proves the editor's wiring and display, not the game numbers
/// (RECURRING_TRAPS 2.11).
/// </summary>
public sealed class EngineOracle : IDisposable
{
    private static readonly object InitGate = new();
    private static string? _initializedFrom;
    private static string? _initializedDDragonDir;
    private static int _initializedDetailFiles;

    private readonly ConfigManager _config;
    private readonly ComboEngine _engine;
    private readonly ComboRunner _runner;

    /// <param name="appDir">The app copy (<see cref="AppCopyFixture.AppDir"/>).</param>
    /// <param name="sessionConfigPath">The session's <c>user_config.json</c>; copied into
    /// <paramref name="scratchDir"/> so the oracle never writes the app's file.</param>
    public EngineOracle(string appDir, string sessionConfigPath, string scratchDir)
    {
        InitializeRepositories(appDir);
        Directory.CreateDirectory(scratchDir);
        string copy = Path.Combine(scratchDir, "user_config.json");
        File.Copy(sessionConfigPath, copy, overwrite: true);
        _config = new ConfigManager(copy);
        // As AppComposition.StartSubsystems: one RuneEngine shared by the ComboEngine and the runner.
        var runeEngine = new RuneEngine();
        _engine = new ComboEngine(new DamageEngine(), runeEngine);
        // No live game: ComputePreview falls back to its stated out-of-game reference.
        _runner = new ComboRunner(_engine, _config, () => null, runeEngine: runeEngine);
    }

    /// <summary>The Data Dragon version the repositories were last loaded from.</summary>
    public static string? DDragonVersion => _initializedDDragonDir is { } d ? Path.GetFileName(d) : null;

    /// <summary>Champion, item and rune repositories from the app copy's data dirs. Loaded once per
    /// process, and again when the copy's newest complete Data Dragon folder or its number of champion
    /// detail files changed (the app downloads the live patch into it and uses it from its next launch,
    /// DDragonUpdater).</summary>
    private static void InitializeRepositories(string appDir)
    {
        lock (InitGate)
        {
            if (_initializedFrom is not null
                && !string.Equals(_initializedFrom, appDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Oracle already initialised from {_initializedFrom}");

            // Program.Main: the downloaded root (AppPaths.AssetCacheDir\ddragon, which for a
            // non-installed copy is data\ddragon next to the exe) first, then the bundled one.
            string ddragonRoot = Path.Combine(appDir, "data", "ddragon");
            DDragonData.SetRoots(ddragonRoot, ddragonRoot);

            // AppComposition.InitializeAsync, the M11 block.
            var ddragonCacheDir = DDragonData.CurrentDir
                ?? Path.Combine(appDir, "data", "ddragon", DDragonData.BundledVersion);
            // AI-NOTE: (lolF02 t6a, HC:126-132) the app writes the per-champion detail files of a new
            // Data Dragon after its restart onto it. A load before they exist builds every champion from
            // the summary, whose attackdamageperlevel the live patch ships zeroed (AbilityIconProvider
            // repairs only the detail files), so every AD-based number read low until the next process.
            // Reload when the folder's detail-file count changed, not only its name.
            int detailFiles = DetailFileCount(ddragonCacheDir);
            if (_initializedFrom is not null
                && string.Equals(_initializedDDragonDir, ddragonCacheDir, StringComparison.OrdinalIgnoreCase)
                && _initializedDetailFiles == detailFiles)
                return;

            var itemKoPath = Path.Combine(ddragonCacheDir, "item.ko_KR.json");
            var itemKoJson = File.Exists(itemKoPath) ? File.ReadAllText(itemKoPath) : null;
            ItemRepository.Initialize(
                DDragonParser.ParseItems(File.ReadAllText(Path.Combine(ddragonCacheDir, "item.json")), itemKoJson));
            RuneRepository.Initialize(
                DDragonParser.ParseRunes(File.ReadAllText(Path.Combine(ddragonCacheDir, "runesReforged.json"))));

            var communityDragonCacheDir = Path.Combine(appDir, "data", "communitydragon");
            string? specialPropertiesDir = Path.Combine(appDir, "Data", "special_properties");
            if (!Directory.Exists(specialPropertiesDir)) specialPropertiesDir = null;
            ChampionRepository.InitializeFromCache(ddragonCacheDir, communityDragonCacheDir, specialPropertiesDir);

            _initializedFrom = appDir;
            _initializedDDragonDir = ddragonCacheDir;
            _initializedDetailFiles = detailFiles;
        }
    }

    private static int DetailFileCount(string ddragonCacheDir)
    {
        string dir = Path.Combine(ddragonCacheDir, "champion");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0;
    }

    /// <summary><c>ComboRunner.ComputePreview</c> for <paramref name="graph"/>; null when nothing
    /// could be computed.</summary>
    public ComboPreview? Preview(string championId, ComboGraph graph) => _runner.ComputePreview(championId, graph);

    /// <summary>The combo the editor saved as <c>combos.saved.&lt;comboId&gt;</c> in
    /// <paramref name="config"/> (a session's <c>user_config.json</c>): its champion and its graph,
    /// deserialised the way <c>ComboEditor.LoadCombo</c> does.</summary>
    public (string ChampionId, ComboGraph Graph) FromSavedCombo(JsonObject config, string comboId)
    {
        string json = ConfigSeed.Get(config, "combos.saved")?[comboId]?.GetValue<string>()
                      ?? throw new KeyNotFoundException($"No combos.saved.{comboId} in the session config");
        var saved = JsonSerializer.Deserialize<SavedCombo>(json)
                    ?? throw new InvalidOperationException($"combos.saved.{comboId} is not a SavedCombo");
        return (saved.ChampionId, _engine.Deserialize(saved.GraphJson));
    }

    /// <summary>A graph of plain skill nodes, as the editor builds them from palette chips.</summary>
    public static ComboGraph SkillSequence(params string[] slots)
    {
        var nodes = slots.Select((slot, i) => new ComboNode(
            Id: $"{slot}_{i}", NodeType: ComboNodeType.Skill, Name: slot,
            Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0)).ToList();
        return new ComboGraph(nodes, Array.Empty<ComboEdge>());
    }

    public void Dispose()
    {
        _runner.Dispose();
        _config.Dispose();
    }
}
