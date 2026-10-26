using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Overlay.Client.UiTests.M45;

/// <summary>
/// The M45 editor cases (<c>M45/m45_cases.json</c>, README "M45 editor cases"): one case per
/// HUMAN_CHECKLIST.md line, one claim per champion / ability / statement of the line.
/// </summary>
public sealed record M45CaseFile(IReadOnlyList<M45Case> Cases)
{
    public const string FileName = "m45_cases.json";

    /// <summary>The engine evidence that closes a part on the M45 tooltip audit (fidelity F = 100).</summary>
    public const string M45Fidelity = "M45 F=100 @40bbddd";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The file copied next to the test assembly.</summary>
    public static M45CaseFile Load() =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "M45", FileName)));

    public static M45CaseFile Parse(string json) =>
        JsonSerializer.Deserialize<M45CaseFile>(json, Options) ?? throw new InvalidDataException("empty case file");

    public M45Case Case(int hc) => Cases.Single(c => c.Hc == hc);
}

public sealed record M45Case(int Hc, string Line, IReadOnlyList<M45Claim> Claims);

/// <summary>One claim: a champion, the palette slots dragged into the sequence, the knob states it
/// reads the preview in, and its checks. <see cref="ChampionKo"/> is the champion tile's label.</summary>
public sealed record M45Claim(
    string Id, string Champion, string ChampionKo, IReadOnlyList<string> Sequence,
    IReadOnlyList<M45State> States, IReadOnlyList<M45Check> Checks, string? EngineEvidence, string? Note)
{
    public IReadOnlyList<string> SequenceOf(M45State state) => state.Sequence ?? Sequence;
}

/// <summary>A knob state. <see cref="Sequence"/>, when set, replaces the claim's sequence for this state.</summary>
public sealed record M45State(string Name, IReadOnlyList<M45Knob> Knobs, IReadOnlyList<string>? Sequence = null);

/// <summary><see cref="Kind"/>: attackCount | hitDuration | distance (percent) | stackCount | condition
/// (true) | fx (the '＋fx' row label). <see cref="Node"/> is the 0-based sequence index.</summary>
public sealed record M45Knob(int Node, string Kind, JsonElement Value)
{
    public double Number => Value.GetDouble();

    public string Text => Value.ValueKind == JsonValueKind.String ? Value.GetString()! : Value.ToString();
}

/// <summary><see cref="Type"/>: badge | noBadge | sliderMax | paletteHas | compare | oracle | card.
/// A card check (lolF02 t6b) names two states in <see cref="A"/> and <see cref="B"/> (plain state names),
/// <see cref="Op"/> &gt; or &lt;, and the overlay combo card line it reads by its <see cref="Label"/>.</summary>
public sealed record M45Check(
    string Type, int? Node = null, string? Kind = null, double? Max = null, string? Slot = null,
    string? A = null, string? Op = null, string? B = null, double? Value = null,
    IReadOnlyList<string>? States = null, string? Label = null)
{
    public override string ToString() => Type switch
    {
        "compare" => $"compare {A} {Op}{(Value is { } v ? " " + v.ToString(CultureInfo.InvariantCulture) : "")} {B}",
        "badge" or "noBadge" => $"{Type} {Kind} on node {Node}",
        "sliderMax" => $"sliderMax {Kind} on node {Node} = {Max?.ToString(CultureInfo.InvariantCulture)}",
        "paletteHas" => $"paletteHas {Slot}",
        "card" => $"card '{Label}' {A} {Op} {B}",
        _ => Type,
    };
}

/// <summary>What <c>PreviewRange</c> showed in one state: <c>"{Min:N0} ~ {Max:N0}"</c> or <c>"{Resolved:N0}"</c>.</summary>
public sealed record M45Reading(string Text, double Min, double Max, bool IsRange)
{
    /// <summary>The single number, or a range's lower end (the editor's resolved number is the floor:
    /// every knob left unset reads its conservative value).</summary>
    public double Resolved => Min;

    /// <summary>Parses the preview text in <paramref name="culture"/> (the app formats with N0 in its
    /// current culture).</summary>
    public static M45Reading Parse(string text, CultureInfo culture)
    {
        var parts = text.Split('~', StringSplitOptions.TrimEntries);
        double Num(string s) => double.Parse(s, NumberStyles.Number, culture);
        return parts.Length switch
        {
            1 => new M45Reading(text, Num(parts[0]), Num(parts[0]), false),
            2 => new M45Reading(text, Num(parts[0]), Num(parts[1]), true),
            _ => throw new FormatException($"PreviewRange '{text}' is neither a number nor a range"),
        };
    }

    public double Field(string field) => field switch
    {
        "min" => Min,
        "max" => Max,
        "resolved" => Resolved,
        _ => throw new ArgumentException($"unknown field '{field}' (min | max | resolved)"),
    };
}

/// <summary>The card check's reading of an OCR'd combo card line.</summary>
public static class M45CardLine
{
    /// <summary>The HP number of the line holding <paramref name="label"/> in <paramref name="ocr"/>, or null.
    /// The line reads "≤ N HP 확정킬 (label)" (OverlayHost.BuildExecuteLine); spaces and line breaks are
    /// dropped first, since Windows OCR splits Hangul into words.</summary>
    /// <remarks>AI-NOTE: the last "N HP" ahead of the label is taken, whatever OCR makes of the "≤".</remarks>
    public static double? Value(string ocr, string label)
    {
        static string Squash(string s) => Regex.Replace(s, @"\s+", "");
        string text = Squash(ocr);
        int at = text.IndexOf(Squash(label), StringComparison.Ordinal);
        if (at < 0) return null;
        var match = Regex.Match(text[..at], @"(\d[\d,]*)HP", RegexOptions.RightToLeft);
        return match.Success ? double.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>The compare check:<c>a</c> and <c>b</c> are sums of <c>state.field</c> terms and numbers
/// (<c>"five.resolved - four.resolved"</c>, <c>"0"</c>).</summary>
public static class M45Compare
{
    /// <summary>Relative tolerance of <c>=</c> and <c>ratio</c> (0.5%).</summary>
    public const double RelativeTolerance = 0.005;

    /// <summary>Absolute slack per displayed number in a term: the preview rounds to whole numbers.</summary>
    public const double RoundingPerTerm = 0.5;

    public static (double Value, int DisplayedTerms) Evaluate(string expression, IReadOnlyDictionary<string, M45Reading> readings)
    {
        double total = 0;
        int terms = 0;
        var tokens = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double sign = 1;
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            if (t == "+") { sign = 1; continue; }
            if (t == "-") { sign = -1; continue; }
            double v;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
                v = literal;
            else
            {
                int dot = t.IndexOf('.');
                if (dot <= 0) throw new FormatException($"'{t}' in '{expression}' is not state.field or a number");
                string state = t[..dot];
                if (!readings.TryGetValue(state, out var reading))
                    throw new KeyNotFoundException($"'{expression}' names state '{state}', which was not read");
                v = reading.Field(t[(dot + 1)..]);
                terms++;
            }
            total += sign * v;
            sign = 1;
        }
        return (total, terms);
    }

    /// <summary>Null when the check holds, else what was found.</summary>
    public static string? Check(M45Check check, IReadOnlyDictionary<string, M45Reading> readings)
    {
        var (a, ta) = Evaluate(check.A ?? throw new ArgumentException("compare needs a"), readings);
        var (b, tb) = Evaluate(check.B ?? throw new ArgumentException("compare needs b"), readings);
        double slack = RoundingPerTerm * (ta + tb);
        bool ok;
        string found;
        switch (check.Op)
        {
            case ">":
                ok = a > b;
                found = $"{a:0.##} is not > {b:0.##}";
                break;
            case "<":
                ok = a < b;
                found = $"{a:0.##} is not < {b:0.##}";
                break;
            case "=":
                ok = Math.Abs(a - b) <= Math.Max(RelativeTolerance * Math.Max(Math.Abs(a), Math.Abs(b)), slack);
                found = $"{a:0.##} != {b:0.##}";
                break;
            case "ratio":
                double value = check.Value ?? throw new ArgumentException("ratio needs value");
                double expected = value * b;
                // AI-NOTE: the rounding of b is scaled by the ratio, like b itself.
                double ratioSlack = RoundingPerTerm * (ta + tb * value);
                ok = Math.Abs(a - expected) <= Math.Max(RelativeTolerance * Math.Abs(expected), ratioSlack);
                found = $"{a:0.##} / {b:0.##} = {(b == 0 ? double.NaN : a / b):0.####}, expected {value.ToString(CultureInfo.InvariantCulture)}";
                break;
            default:
                throw new ArgumentException($"unknown compare op '{check.Op}' (> < = ratio)");
        }
        return ok ? null : found;
    }
}
