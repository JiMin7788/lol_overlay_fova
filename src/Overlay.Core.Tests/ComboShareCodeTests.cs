using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>Combo share codes (ComboShareCode + ComboEditor.ShareCombo/ImportSharedCombo):
/// round-trip, Discord-message extraction, Discord-safe alphabet and length, rejection paths, and
/// that a failed import writes nothing. Same temp-config isolation as ComboEditorTests.</summary>
public class ComboShareCodeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboShareCodeTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "ComboShareCodeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private ComboEditor NewEditor(out ConfigManager config)
    {
        config = new ConfigManager(_configPath);
        return new ComboEditor(new ComboEngine(new DamageEngine(), new RuneEngine()), config);
    }

    private static ComboNode Node(string id, ComboNodeType type = ComboNodeType.Skill) => new(
        Id: id, NodeType: type, Name: id, Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private static string SavedComboId(ComboEditor editor, string champion, string name, params string[] slots)
    {
        var draft = editor.CreateCombo(champion, name);
        for (int i = 0; i < slots.Length; i++)
            editor.AddNode(draft.Id, Node($"{slots[i]}_{i}", slots[i] == "AA" ? ComboNodeType.Aa : ComboNodeType.Skill));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    private static int SavedCount(ConfigManager config)
        => config.Get("combos.saved") is IDictionary<string, object?> saved ? saved.Count : 0;

    private static string RawCode(string json)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            deflate.Write(bytes, 0, bytes.Length);
        }
        return ComboShareCode.Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=');
    }

    [Fact]
    public void Encode_Decode_RoundTripsChampionNameAndGraph()
    {
        const string graph = "{\"Nodes\":[{\"Id\":\"Q_0\"}],\"Edges\":[]}";
        var code = ComboShareCode.Encode("Zed", "6렙 올인", graph);

        var shared = ComboShareCode.Decode(code);

        Assert.Equal("Zed", shared.ChampionId);
        Assert.Equal("6렙 올인", shared.Name);
        Assert.Equal(JsonDocument.Parse(graph).RootElement.GetRawText(),
                     JsonDocument.Parse(shared.GraphJson).RootElement.GetRawText());
    }

    [Fact]
    public void Decode_FindsCodeInsideTheWholeDiscordMessage()
    {
        var code = ComboShareCode.Encode("Ahri", "E-Q-R", "{\"Nodes\":[],\"Edges\":[]}");
        var message = "누가 이거 써봐요\n" + ComboShareCode.ToDiscordMessage("아리", "E-Q-R", code) + "\nㄱㄱ";

        Assert.True(ComboShareCode.ContainsCode(message));
        Assert.Equal("Ahri", ComboShareCode.Decode(message).ChampionId);
    }

    [Fact]
    public void Code_IsDiscordSafe_AndFitsOneMessage()
    {
        var editor = NewEditor(out var config);
        var slots = Enumerable.Range(0, 16).Select(i => new[] { "Q", "W", "E", "R", "AA" }[i % 5]).ToArray();
        var id = SavedComboId(editor, "Locke", "긴 콤보 테스트", slots);

        var code = editor.ShareCombo(id);
        var message = ComboShareCode.ToDiscordMessage("로크", "긴 콤보 테스트", code);

        Assert.StartsWith(ComboShareCode.Prefix, code);
        Assert.DoesNotContain('_', code);   // Discord italics would eat it
        Assert.DoesNotContain('=', code);
        Assert.True(message.Length < 2000, $"message is {message.Length} chars"); // Discord limit
        config.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("그냥 채팅")]
    public void Decode_NoCode_IsNotFound(string? text)
    {
        var ex = Assert.Throws<ComboShareCodeException>(() => ComboShareCode.Decode(text));
        Assert.Equal(ComboShareCodeError.NotFound, ex.Error);
    }

    [Fact]
    public void Decode_DamagedCode_IsCorrupt()
    {
        var ex = Assert.Throws<ComboShareCodeException>(() => ComboShareCode.Decode("FOVA1:AAAAAAAAAAAA"));
        Assert.Equal(ComboShareCodeError.Corrupt, ex.Error);
    }

    [Fact]
    public void Decode_NewerVersion_AsksForUpdate()
    {
        var code = RawCode("{\"v\":2,\"c\":\"Zed\",\"n\":\"x\",\"g\":{\"Nodes\":[],\"Edges\":[]}}");
        var ex = Assert.Throws<ComboShareCodeException>(() => ComboShareCode.Decode(code));
        Assert.Equal(ComboShareCodeError.NewerVersion, ex.Error);
    }

    [Fact]
    public void Decode_DropsNewlinesAndCapsName()
    {
        var code = ComboShareCode.Encode("Zed", "a\nb" + new string('x', 100), "{\"Nodes\":[],\"Edges\":[]}");
        var name = ComboShareCode.Decode(code).Name;
        Assert.DoesNotContain('\n', name);
        Assert.True(name.Length <= ComboShareCode.MaxNameLength);
    }

    [Fact]
    public void ImportSharedCombo_SavesANewComboWithSameSequenceAndNoHotkey()
    {
        var editor = NewEditor(out var config);
        var originalId = SavedComboId(editor, "Zed", "올인", "W", "E", "Q", "R", "AA");
        editor.BindHotkey(originalId, "ALT+1");
        var code = editor.ShareCombo(originalId);

        var imported = editor.ImportSharedCombo("받은 메시지: " + code);

        Assert.NotEqual(originalId, imported.Id);
        Assert.Equal("Zed", imported.ChampionId);
        Assert.Equal("올인", imported.Name);
        Assert.Equal(new[] { "W_0", "E_1", "Q_2", "R_3", "AA_4" }, imported.Graph.Nodes.Select(n => n.Id));
        Assert.Equal(2, SavedCount(config));
        Assert.DoesNotContain(imported.Id,
            (config.Get("hotkeys.comboSlots") as IDictionary<string, object?> ?? new Dictionary<string, object?>())
                .Values.Select(v => v?.ToString()));
        config.Dispose();
    }

    [Fact]
    public void ImportSharedCombo_InvalidGraph_WritesNothing()
    {
        var editor = NewEditor(out var config);
        var code = ComboShareCode.Encode("Zed", "빈 콤보", "{\"Nodes\":[],\"Edges\":[]}");

        Assert.Throws<ComboImportException>(() => editor.ImportSharedCombo(code));
        Assert.Equal(0, SavedCount(config));
        config.Dispose();
    }
}
