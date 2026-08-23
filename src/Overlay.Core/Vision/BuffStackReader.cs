namespace Overlay.Core.Vision;

/// <summary>
/// (loop 540) Reads the stack COUNT off the player's own buff bar for a stack-buff ability —
/// the motivating case is Nasus Q Siphoning Strike, whose count the Live Client API does not
/// expose anywhere (verified live against every endpoint of its OpenAPI spec), while the game
/// renders it plainly on the buff icon. P1/P3-clean: pixels of the player's OWN screen, no
/// memory access, display-only consumption (the count feeds
/// <see cref="Combo.ComboRunner.LiveStackProvider"/>).
///
/// <para>Pure byte-buffer logic like <see cref="MinimapDetector"/> (the Client decodes images
/// and captures frames; this class never touches WPF/GDI), so it unit-tests against real
/// captured fixtures. Two stages:</para>
///
/// <para><b>1. Find the buff icon.</b> The buff row's position is NOT fixed — the row shifts up
/// when the level-up chevrons appear, and the icon's slot shifts right as other buffs precede
/// it — so the icon is template-matched across the whole band (2px coarse scan + 1px refine),
/// comparing only the TOP 55% of the icon: the stack digits are drawn OVER its bottom half, so
/// including those rows would penalize exactly the frames this exists to read. The template is
/// the ability's own square spell icon (Data Dragon), downscaled by the caller to the expected
/// on-screen buff-icon size.</para>
///
/// <para><b>2. Read the digits.</b> The count renders in light-gray/white with a dark outline,
/// anchored at the icon's bottom-left and OVERFLOWING its right edge for wide values (a real
/// "112" reaches ~1.6× the icon's width), so the digit strip spans [icon.x-1, icon.x+1.9×size]
/// × [icon.y+0.45×size, icon.y+size+6]. Near-white pixels (min channel ≥ 120, channel spread
/// ≤ 60 — tuned on real 1080p captures) are kept, speckle is dropped by connected-component
/// size/height filters, glyphs are split on empty column gaps, normalized to a 6×10 grid, and
/// matched to <see cref="GlyphLibrary"/> by Hamming distance. Any unrecognized glyph fails the
/// WHOLE read (returns null) — a partially-read number like "12" for "112" is worse than no
/// reading, since the caller falls back to the editor knob / 0-stack floor honestly.</para>
/// </summary>
public sealed class BuffStackReader
{
    /// <summary>Normalized glyph grid width/height. 6×10 keeps every digit's distinguishing
    /// stroke while tolerating the 1-2px anti-aliasing jitter between frames.</summary>
    public const int GridW = 6, GridH = 10;

    private readonly byte[] _templateBgra; // top MatchRows rows of the icon template, BGRA
    private readonly int _size;            // icon side in px (template is _size×_size, rows cropped)
    private readonly int _matchRows;

    /// <summary>Fraction of the icon's top rows used for template matching (digits overlay the rest).</summary>
    private const double MatchFraction = 0.55;

    /// <summary>Minimum similarity (1 − meanRGBdistance/442) for an icon match. Real captures
    /// score ≈0.81; unrelated background under 0.6. Between them with margin.</summary>
    private const double MatchThreshold = 0.70;

    // Digit-pixel gate + speckle filters (tuned on the loop-540 1080p fixture corpus).
    private const int DigitMinChannel = 120;
    private const int DigitMaxSpread = 60;
    private const int MinComponentPixels = 6;
    private const int MinComponentHeight = 6;
    private const int MinGlyphWidth = 2;

    /// <summary>Max Hamming mismatches (of 60 grid cells) for a glyph to classify, and the
    /// minimum lead over the nearest OTHER-digit pattern (same-digit variants never disqualify
    /// each other) so a between-two-digits blob stays unread. Both tuned on the loop-540 corpus:
    /// 672 of 720 frames readable, one single-frame misread, stacks 112→2230 monotonic.</summary>
    private const int MaxGlyphMismatch = 9;
    private const int MinGlyphMargin = 2;

    /// <summary>Real digits within one count sit 2-5 columns apart (measured: 1293× gap 2, 332×
    /// gap 4, 18× gap 5). A glyph further right than this is the NEIGHBORING buff's art bleeding
    /// into the strip (measured at gap 14) — reading stops there, keeping the digits before it.</summary>
    private const int MaxGlyphGap = 5;

    /// <param name="iconTemplateBgra">The ability's square icon, BGRA, already downscaled to
    /// <paramref name="iconSizePx"/>² (the expected on-screen buff-icon size — ~25px at 1080p).
    /// The caller (Client) owns image decode, mirroring <see cref="EnemyTemplate.FromSquareIcon"/>.</param>
    public BuffStackReader(byte[] iconTemplateBgra, int iconSizePx)
    {
        if (iconTemplateBgra.Length < iconSizePx * iconSizePx * 4)
            throw new ArgumentException("template smaller than iconSizePx²", nameof(iconTemplateBgra));
        _size = iconSizePx;
        _matchRows = Math.Max(1, (int)(iconSizePx * MatchFraction));
        _templateBgra = iconTemplateBgra;
    }

    /// <summary>Finds the buff icon in the band and reads its stack digits. Returns null when the
    /// icon is not on screen (no buff yet / band missed) or any glyph fails to classify.</summary>
    public int? ReadStacks(byte[] bgra, int width, int height, int stride)
    {
        var (x, y, score) = FindIcon(bgra, width, height, stride);
        if (score < MatchThreshold) return null;
        return ReadDigitsAt(bgra, width, height, stride, x, y);
    }

    /// <summary>Best-scoring icon position (coarse 2px scan, 1px refinement) — exposed for the
    /// fixture tests so a threshold regression names the score it saw.</summary>
    public (int X, int Y, double Score) FindIcon(byte[] bgra, int width, int height, int stride)
    {
        int bestX = -1, bestY = -1;
        double bestScore = -1;
        for (int y = 0; y + _size <= height; y += 2)
            for (int x = 0; x + _size <= width; x += 2)
                Consider(x, y, ref bestX, ref bestY, ref bestScore);
        int rx = bestX, ry = bestY;
        for (int y = Math.Max(0, ry - 2); y <= Math.Min(height - _size, ry + 2); y++)
            for (int x = Math.Max(0, rx - 2); x <= Math.Min(width - _size, rx + 2); x++)
                Consider(x, y, ref bestX, ref bestY, ref bestScore);
        return (bestX, bestY, bestScore);

        void Consider(int x, int y, ref int bx, ref int by, ref double bs)
        {
            double dist = 0;
            for (int ty = 0; ty < _matchRows; ty++)
            {
                int rowOff = (y + ty) * stride + x * 4;
                int tplOff = ty * _size * 4;
                for (int tx = 0; tx < _size; tx++)
                {
                    int po = rowOff + tx * 4, to = tplOff + tx * 4;
                    double db = bgra[po] - _templateBgra[to];
                    double dg = bgra[po + 1] - _templateBgra[to + 1];
                    double dr = bgra[po + 2] - _templateBgra[to + 2];
                    dist += Math.Sqrt(db * db + dg * dg + dr * dr);
                }
            }
            double s = 1 - dist / (_matchRows * _size) / 441.7;
            if (s > bs) { bs = s; bx = x; by = y; }
        }
    }

    private int? ReadDigitsAt(byte[] bgra, int width, int height, int stride, int iconX, int iconY)
    {
        int x0 = Math.Max(0, iconX - 1);
        int x1 = Math.Min(width, iconX + (int)(_size * 1.9));
        int y0 = Math.Min(height, iconY + (int)(_size * 0.45));
        int y1 = Math.Min(height, iconY + _size + 6);
        int w = x1 - x0, h = y1 - y0;
        if (w < MinGlyphWidth || h < MinComponentHeight) return null;

        // Near-white gate.
        var mask = new bool[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y0 + y) * stride + (x0 + x) * 4;
                byte b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
                int mn = Math.Min(b, Math.Min(g, r)), mx = Math.Max(b, Math.Max(g, r));
                mask[y, x] = mn >= DigitMinChannel && mx - mn <= DigitMaxSpread;
            }

        RemoveSpeckle(mask, h, w);

        // Column runs (≤1-column gaps bridge anti-aliased strokes within one glyph).
        var glyphs = new List<(int A, int B)>();
        int start = -1, gap = 0;
        for (int x = 0; x <= w; x++)
        {
            bool any = false;
            if (x < w) for (int y = 0; y < h && !any; y++) any = mask[y, x];
            if (any) { if (start < 0) start = x; gap = 0; }
            else if (start >= 0 && ++gap > 1) { glyphs.Add((start, x - gap + 1)); start = -1; gap = 0; }
        }
        if (start >= 0) glyphs.Add((start, w));
        glyphs.RemoveAll(gl => gl.B - gl.A < MinGlyphWidth);
        if (glyphs.Count == 0) return null;

        long value = 0;
        int digits = 0, prevEnd = -1;
        foreach (var (a, b) in glyphs)
        {
            if (prevEnd >= 0 && a - prevEnd > MaxGlyphGap) break; // neighbor-buff artifact — stop
            int? digit = Classify(NormalizeGlyph(mask, h, a, b));
            if (digit is null) return null;   // one unreadable glyph poisons the whole number
            value = value * 10 + digit.Value;
            digits++;
            prevEnd = b;
            if (value > 99999) return null;   // no real stack count is this large — misread
        }
        return digits > 0 ? (int)value : null;
    }

    private static void RemoveSpeckle(bool[,] mask, int h, int w)
    {
        var seen = new bool[h, w];
        var stack = new Stack<(int Y, int X)>();
        var comp = new List<(int Y, int X)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!mask[y, x] || seen[y, x]) continue;
                comp.Clear();
                stack.Push((y, x));
                seen[y, x] = true;
                int minY = y, maxY = y;
                while (stack.Count > 0)
                {
                    var (cy, cx) = stack.Pop();
                    comp.Add((cy, cx));
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int ny = cy + dy, nx = cx + dx;
                            if (ny >= 0 && ny < h && nx >= 0 && nx < w && mask[ny, nx] && !seen[ny, nx])
                            {
                                seen[ny, nx] = true;
                                stack.Push((ny, nx));
                            }
                        }
                }
                if (comp.Count < MinComponentPixels || maxY - minY + 1 < MinComponentHeight)
                    foreach (var (cy, cx) in comp) mask[cy, cx] = false;
            }
    }

    /// <summary>Row-trims one glyph's columns and box-resamples it onto the 6×10 grid: a target
    /// cell is ON when ≥35% of its source rectangle is masked. Mirrored 1:1 by the fixture
    /// tooling that generated <see cref="GlyphLibrary"/> from the same captures.</summary>
    internal static bool[] NormalizeGlyph(bool[,] mask, int maskH, int a, int b)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < maskH; y++)
            for (int x = a; x < b; x++)
                if (mask[y, x]) { if (top < 0) top = y; bottom = y; break; }
        if (top < 0) return new bool[GridW * GridH];
        int gw = b - a, gh = bottom - top + 1;

        var grid = new bool[GridW * GridH];
        for (int j = 0; j < GridH; j++)
            for (int i = 0; i < GridW; i++)
            {
                double sy0 = top + j * (double)gh / GridH, sy1 = top + (j + 1) * (double)gh / GridH;
                double sx0 = a + i * (double)gw / GridW, sx1 = a + (i + 1) * (double)gw / GridW;
                double covered = 0, total = 0;
                for (int y = (int)sy0; y < Math.Ceiling(sy1); y++)
                    for (int x = (int)sx0; x < Math.Ceiling(sx1); x++)
                    {
                        double fy = Math.Min(sy1, y + 1) - Math.Max(sy0, y);
                        double fx = Math.Min(sx1, x + 1) - Math.Max(sx0, x);
                        if (fy <= 0 || fx <= 0) continue;
                        total += fy * fx;
                        if (y <= bottom && x < b && mask[y, x]) covered += fy * fx;
                    }
                grid[j * GridW + i] = total > 0 && covered / total >= 0.35;
            }
        return grid;
    }

    private static int? Classify(bool[] grid)
    {
        int best = -1, bestDist = int.MaxValue;
        foreach (var (digit, tpl) in GlyphLibrary)
        {
            int dist = 0;
            for (int i = 0; i < grid.Length; i++)
                if (grid[i] != tpl[i]) dist++;
            if (dist < bestDist) { bestDist = dist; best = digit; }
        }
        if (best < 0 || bestDist > MaxGlyphMismatch) return null;
        // Margin vs the nearest pattern of a DIFFERENT digit — variants of the same digit are
        // allies, not rivals, so they never disqualify each other.
        int otherMin = int.MaxValue;
        foreach (var (digit, tpl) in GlyphLibrary)
        {
            if (digit == best) continue;
            int dist = 0;
            for (int i = 0; i < grid.Length; i++)
                if (grid[i] != tpl[i]) dist++;
            otherMin = Math.Min(otherMin, dist);
        }
        return otherMin - bestDist >= MinGlyphMargin ? best : null;
    }

    /// <summary>6×10 glyph grids of the buff-count font, extracted from REAL 1080p captures
    /// (loop-540 fixture corpus: 720 frames, Nasus stacks 112→2230). Several digits carry TWO
    /// patterns because the previous digit's dark outline clips the glyph's left edge, so the
    /// same digit renders differently by position ("1" leading vs following, "2" mid vs trailing).
    /// The whole set was validated by classifying every corpus frame and checking the stack
    /// sequence stays monotonic — 672/720 readable, one single-frame misread (which the client's
    /// two-consecutive-reads confirmation suppresses).</summary>
    /// <summary>Labeled from the fixture-corpus cluster sheet — see <see cref="GlyphLibrary"/>.</summary>
    private static readonly (int Digit, string[] Rows)[] GlyphPatterns =
    {
        (1, new[] { "...###", "######", "...###", "...###", "...###", "...###", "...###", "...###", "...###", "...###" }),
        (1, new[] { "#.....", "###..#", ".#####", ".....#", ".....#", ".....#", ".....#", ".....#", ".....#", ".....#" }),
        (2, new[] { ".###..", "#####.", "##..#.", "#....#", ".....#", "....##", "...##.", "..##..", "..#...", ".##..." }),
        (2, new[] { ".###..", "######", "##..##", ".....#", "....##", "...##.", "..##..", ".#....", "#.....", "######" }),
        (3, new[] { ".###..", "#..##.", ".....#", ".....#", "...###", "...###", "....##", ".....#", "#....#", "#####." }),
        (0, new[] { ".####.", "#..###", "#...##", "#....#", "#....#", "#....#", "#....#", "#....#", "#....#", "#####." }),
        (0, new[] { "..###.", ".#..##", ".#...#", ".#...#", "##...#", "##...#", "##...#", ".#...#", ".#...#", ".####." }),
        (6, new[] { "..##..", "..##..", ".###..", ".##...", "#####.", "######", "#...##", "#....#", "#....#", "#####." }),
        (4, new[] { "...##.", "...##.", "..###.", "...##.", ".#.##.", "##.##.", "#..##.", "##.##.", "######", "...###" }),
        (5, new[] { "######", "#.....", "#.....", "#####.", "######", "#...##", ".....#", ".....#", ".....#", "#####." }),
        (7, new[] { "######", "...###", "...##.", "...##.", "...##.", "...##.", "..##..", "..##..", ".##...", ".##..." }),
        (8, new[] { ".###..", "#..###", "#...##", "#....#", "######", "######", "#...##", "#....#", "#....#", "#####." }),
        (9, new[] { ".####.", "#..###", "#...##", "#....#", "######", "######", "..###.", "..###.", "..##..", ".##..." }),
    };

    private static readonly (int Digit, bool[] Grid)[] GlyphLibrary = BuildLibrary();

    private static (int, bool[])[] BuildLibrary()
    {
        var lib = new (int, bool[])[GlyphPatterns.Length];
        for (int p = 0; p < GlyphPatterns.Length; p++)
        {
            var (digit, rows) = GlyphPatterns[p];
            var grid = new bool[GridW * GridH];
            for (int j = 0; j < GridH; j++)
                for (int i = 0; i < GridW; i++)
                    grid[j * GridW + i] = rows[j][i] == '#';
            lib[p] = (digit, grid);
        }
        return lib;
    }


}
