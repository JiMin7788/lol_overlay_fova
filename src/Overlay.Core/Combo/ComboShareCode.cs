using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlay.Core.Combo;

/// <summary>A combo decoded from a share code: champion, display name and the exact
/// <see cref="ComboEngine.Serialize"/> graph JSON the sender saved.</summary>
public sealed record SharedCombo(string ChampionId, string Name, string GraphJson);

/// <summary>
/// Combo share codes (1.0 plan, "Differentiators"): one line of text a user copies out of Fova,
/// pastes into Discord, and another user pastes back into Fova.
///
/// <para><b>Format.</b> <c>FOVA1:</c> + standard base64 (no padding) of a raw-DEFLATE-compressed
/// UTF-8 JSON payload <c>{"v":1,"c":championId,"n":name,"g":graph}</c>. Standard base64 (not
/// base64url) on purpose: Discord markdown treats <c>_</c> as italics, and a user copying the
/// rendered message would silently lose characters. <see cref="ToDiscordMessage"/> also wraps the
/// code in inline-code backticks so nothing in it is rendered.</para>
///
/// <para><b>What is NOT in a code:</b> hotkeys, target snapshots, item builds, runes — only the
/// combo itself. Damage numbers are recomputed by the receiver's own engine from current patch
/// data, so a code made on an older patch still produces current numbers.</para>
///
/// <para><b>Decoding is lenient about surroundings, strict about content.</b> The token is found
/// anywhere in the pasted text (a whole Discord message works), but the payload must decompress
/// within <see cref="MaxPayloadBytes"/>, parse, carry a supported version, and its graph must pass
/// <see cref="ComboEditor.ImportCombo"/>'s schema check before anything is saved.</para>
/// </summary>
public static class ComboShareCode
{
    public const string Prefix = "FOVA1:";
    public const int CurrentVersion = 1;

    /// <summary>Decompressed-size ceiling. Real combos are 3-12 KB uncompressed; this only exists
    /// so a crafted code can't inflate into something huge.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Longest name kept from a code; longer names are cut, not rejected.</summary>
    public const int MaxNameLength = 60;

    private static readonly Regex TokenPattern =
        new(@"FOVA1:([A-Za-z0-9+/_\-]+=*)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Builds a share code for a saved combo.</summary>
    public static string Encode(string championId, string name, string graphJson)
    {
        if (string.IsNullOrWhiteSpace(championId))
            throw new ArgumentException("championId must not be empty", nameof(championId));
        ArgumentNullException.ThrowIfNull(graphJson);

        using var graphDoc = JsonDocument.Parse(graphJson);
        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", CurrentVersion);
            writer.WriteString("c", championId);
            writer.WriteString("n", Truncate(name ?? string.Empty));
            writer.WritePropertyName("g");
            graphDoc.RootElement.WriteTo(writer); // embedded as an object, not a re-escaped string
            writer.WriteEndObject();
        }

        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json.GetBuffer(), 0, (int)json.Length);

        return Prefix + Convert.ToBase64String(packed.GetBuffer(), 0, (int)packed.Length).TrimEnd('=');
    }

    /// <summary>The text the copy button puts on the clipboard: a readable header line plus the
    /// code in inline-code backticks, ready to paste into a Discord channel as-is.</summary>
    /// <paramref name="footer"/> is the localized "how to import" line (rendered small via <c>-#</c>).
    public static string ToDiscordMessage(string championDisplayName, string comboName, string code, string? footer = null)
    {
        var title = string.IsNullOrWhiteSpace(comboName) ? championDisplayName : $"{championDisplayName} · {comboName}";
        var text = $"⚔️ **{EscapeMarkdown(title)}**\n`{code}`";
        return string.IsNullOrWhiteSpace(footer) ? text : $"{text}\n-# {EscapeMarkdown(footer)}";
    }

    /// <summary>True when <paramref name="text"/> contains something shaped like a share code.</summary>
    public static bool ContainsCode(string? text) => text is not null && TokenPattern.IsMatch(text);

    /// <summary>Finds the first share code in <paramref name="text"/> and decodes it. Throws
    /// <see cref="ComboShareCodeException"/> with a user-presentable reason on any failure.</summary>
    public static SharedCombo Decode(string? text)
    {
        var match = text is null ? Match.Empty : TokenPattern.Match(text);
        if (!match.Success)
            throw new ComboShareCodeException(ComboShareCodeError.NotFound, "No FOVA1: code found.");

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(Repad(match.Groups[1].Value));
        }
        catch (FormatException)
        {
            throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code is damaged (base64).");
        }

        byte[] payload = Inflate(packed);

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code is damaged (shape).");

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number)
                throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code has no version.");
            if (v.GetInt32() > CurrentVersion)
                throw new ComboShareCodeException(ComboShareCodeError.NewerVersion,
                    "The code was made by a newer Fova. Update to import it.");

            var champion = root.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(champion))
                throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code has no champion.");

            var name = root.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

            if (!root.TryGetProperty("g", out var g) || g.ValueKind != JsonValueKind.Object)
                throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code has no combo.");

            return new SharedCombo(champion!, Truncate(name ?? string.Empty), g.GetRawText());
        }
        catch (JsonException)
        {
            throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code is damaged (json).");
        }
    }

    private static byte[] Inflate(byte[] packed)
    {
        try
        {
            using var input = new MemoryStream(packed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxPayloadBytes)
                    throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code is too large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new ComboShareCodeException(ComboShareCodeError.Corrupt, "The code is damaged (deflate).");
        }
    }

    /// <summary>Accepts base64url too (in case a code passed through something that rewrote it)
    /// and restores the padding <see cref="Encode"/> stripped.</summary>
    private static string Repad(string s)
    {
        var b = new StringBuilder(s.TrimEnd('=')).Replace('-', '+').Replace('_', '/');
        while (b.Length % 4 != 0) b.Append('=');
        return b.ToString();
    }

    private static string Truncate(string name)
    {
        var oneLine = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= MaxNameLength ? oneLine : oneLine[..MaxNameLength];
    }

    private static string EscapeMarkdown(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if ("\\*_~`|>#[]()".IndexOf(ch) >= 0) b.Append('\\');
            b.Append(ch);
        }
        return b.ToString();
    }
}

public enum ComboShareCodeError { NotFound, Corrupt, NewerVersion }

/// <summary>Thrown by <see cref="ComboShareCode.Decode"/>; <see cref="Error"/> lets the UI pick a
/// localized message instead of showing the English one.</summary>
public sealed class ComboShareCodeException : Exception
{
    public ComboShareCodeError Error { get; }
    public ComboShareCodeException(ComboShareCodeError error, string message) : base(message) => Error = error;
}
