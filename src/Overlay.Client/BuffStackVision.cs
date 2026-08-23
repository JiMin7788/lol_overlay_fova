#if !LIGHT
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Vision;

namespace Overlay.Client;

/// <summary>
/// (loop 540) Live caster-buff STACK reading off the player's own buff bar — the real-time source
/// behind <see cref="Overlay.Core.Combo.ComboRunner.LiveStackProvider"/>. The Live Client API
/// exposes no buff data at all (verified against its OpenAPI spec: no endpoint, no field), so the
/// count the game renders on the buff icon (Nasus Q Siphoning Strike) is read from pixels instead:
/// once a second, the game window is captured via <c>PrintWindow(PW_RENDERFULLCONTENT)</c> (works
/// for the windowed/borderless modes the overlay supports), the buff-bar BAND of the client area
/// is cropped, and <see cref="BuffStackReader"/> finds the buff icon and reads its digits.
///
/// <para>FULL build only (<c>#if !LIGHT</c>): the public light build ships no screen-reading code
/// by policy. P1/P3-clean — the player's own screen, display-only consumption.</para>
///
/// <para>A value is published only after TWO consecutive ticks read the SAME number: the corpus
/// validation saw one single-frame misread in 672 reads (a "3" grazing a "2"), and stacks change
/// slowly enough that a 1-tick confirmation delay is invisible while it suppresses exactly that
/// class of fluke. Published changes raise <c>GAME.BUFF_STACKS_CHANGED</c>, which
/// <see cref="Overlay.Core.Combo.ComboRunner"/> already treats like any live stat change
/// (re-resolving a shown combo card).</para>
/// </summary>
public sealed class BuffStackVision : IDisposable
{
    /// <summary>Champions with a curated caster-side stack buff worth reading, mapped to the slot
    /// whose <c>stackScaled</c> hit consumes the count and the Data Dragon spell-icon file the
    /// buff icon uses (the buff bar reuses the ability's own art). Extending live stack reading to
    /// another stacker is ONE entry here (plus their curation's stackScaled flag).</summary>
    private static readonly Dictionary<string, (string Slot, string IconFile)> StackChampions =
        new(StringComparer.OrdinalIgnoreCase) { ["Nasus"] = ("Q", "NasusQ.png") };

    // Buff-bar band as fractions of the CLIENT area, measured on the loop-540 1080p corpus: the
    // buff row sits at y 0.79-0.91 normally and shifts ~55px UP when the level-up chevrons appear,
    // and the icon drifts right as other buffs precede it — the band covers all of it.
    private const double BandX0 = 0.28, BandX1 = 0.76, BandY0 = 0.76, BandY1 = 0.94;

    /// <summary>Buff-icon side as a fraction of client height (25px at 1080).</summary>
    private const double IconFraction = 25.0 / 1080.0;

    private const int TickMs = 1000;

    private readonly Func<IntPtr> _getGameWindow;
    private readonly Func<GameSnapshot?> _snapshot;
    private readonly string _ddragonVersion;
    private readonly Action<string>? _log;

    private Timer? _timer;
    private volatile bool _disposed;
    private bool _ticking; // re-entrancy latch; ticks are quick (~10ms) but PrintWindow can stall

    private BuffStackReader? _reader;
    private string? _readerChampion;
    private int _readerIconPx;

    private int? _lastRead;      // previous tick's raw read (confirmation window)
    private volatile int _published = -1; // -1 = nothing published
    private string? _publishedChampion;
    private string? _publishedSlot;

    public BuffStackVision(Func<IntPtr> getGameWindow, Func<GameSnapshot?> snapshot,
        string ddragonVersion, Action<string>? log = null)
    {
        _getGameWindow = getGameWindow;
        _snapshot = snapshot;
        _ddragonVersion = ddragonVersion;
        _log = log;
    }

    public void Start() => _timer ??= new Timer(_ => SafeTick(), null, TickMs, TickMs);

    /// <summary>The last confirmed stack count for (championId, slot), or null — the
    /// <see cref="Overlay.Core.Combo.ComboRunner.LiveStackProvider"/> hook. Volatile read; safe
    /// from any thread.</summary>
    public int? CurrentStacks(string championId, string slot)
    {
        int v = _published;
        return v >= 0
               && string.Equals(championId, _publishedChampion, StringComparison.OrdinalIgnoreCase)
               && string.Equals(slot, _publishedSlot, StringComparison.OrdinalIgnoreCase)
            ? v : null;
    }

    private void SafeTick()
    {
        if (_disposed || _ticking) return;
        _ticking = true;
        try { Tick(); }
        catch (Exception ex) { _log?.Invoke($"buffstacks: tick failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _ticking = false; }
    }

    private void Tick()
    {
        var snap = _snapshot();
        if (snap is not { HasData: true }) { Clear(); return; }

        string? champion = ResolveActiveChampion(snap);
        if (champion is null || !StackChampions.TryGetValue(champion, out var cfg)) { Clear(); return; }

        IntPtr hwnd = _getGameWindow();
        if (hwnd == IntPtr.Zero) { Clear(); return; }

        // (loop 541 fix — user "반영안됨") The WHOLE geometry+capture block runs with this thread
        // temporarily DPI-UNAWARE. The game client is a DPI-unaware process: on a scaled monitor
        // (the user's 4K at 150%) OUR per-monitor-aware view reported its client as 2880×1620
        // physical, but PrintWindow paints only the window's LOGICAL content (1920×1080), so the
        // buff-bar band cropped from the physical rect landed in never-painted transparent pixels
        // and every read was null. An unaware thread sees the same window as 1920×1080 AND gets a
        // fully-painted PrintWindow at exactly that size — self-consistent regardless of the
        // game's own awareness, and the same coordinate space the glyph library's 720-frame
        // fixture corpus was captured in. Restored in finally; this timer thread runs no WPF UI.
        IntPtr prevCtx = SetThreadDpiAwarenessContext(DpiAwarenessContextUnaware);
        try
        {
            TickCapture(hwnd, champion, cfg);
        }
        finally
        {
            if (prevCtx != IntPtr.Zero) SetThreadDpiAwarenessContext(prevCtx);
        }
    }

    private void TickCapture(IntPtr hwnd, string champion, (string Slot, string IconFile) cfg)
    {
        if (!GetClientRect(hwnd, out var client) || client.Right <= 0 || client.Bottom <= 0) return;

        int iconPx = Math.Max(12, (int)Math.Round(client.Bottom * IconFraction));
        if (_reader is null || _readerChampion != champion || _readerIconPx != iconPx)
        {
            _reader = BuildReader(cfg.IconFile, iconPx);
            _readerChampion = champion;
            _readerIconPx = iconPx;
            if (_reader is null) return;
            _log?.Invoke($"buffstacks: reader ready for {champion} {cfg.Slot} (icon {iconPx}px, client {client.Right}x{client.Bottom})");
        }
        if (_reader is null) return;

        var band = CaptureClientBand(hwnd, client.Right, client.Bottom);
        if (band is null) return;

        int? stacks = _reader.ReadStacks(band.Value.Bgra, band.Value.W, band.Value.H, band.Value.W * 4);

        // Two-consecutive-reads confirmation, then publish on change only.
        if (stacks is int s && _lastRead == s && _published != s)
        {
            _published = s;
            _publishedChampion = champion;
            _publishedSlot = cfg.Slot;
            _log?.Invoke($"buffstacks: {champion} {cfg.Slot} = {s}");
            Overlay.Core.EventBus.EventBus.Publish("GAME.BUFF_STACKS_CHANGED", s, "BuffStackVision");
        }
        _lastRead = stacks;
    }

    private void Clear()
    {
        _lastRead = null;
        _published = -1;
        _publishedChampion = null;
        _publishedSlot = null;
    }

    private static string? ResolveActiveChampion(GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (SamePlayer(p.SummonerName, snap.ActivePlayerSummonerName)
                || SamePlayer(p.RiotId, snap.ActivePlayerSummonerName))
                return ChampionSummary.ResolveKoreanName(p.ChampionName) ?? p.ChampionName;
        }
        return null;
    }

    private static bool SamePlayer(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        static string Base(string s) { int h = s.IndexOf('#'); return h < 0 ? s : s[..h]; }
        return string.Equals(Base(a), Base(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes the ability's Data Dragon spell icon and downscales it to the on-screen
    /// buff-icon size (same WPF decode path as <see cref="MinimapVisionPipeline"/>'s portraits).</summary>
    private BuffStackReader? BuildReader(string iconFile, int iconPx)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", _ddragonVersion,
                "img", "spell", iconFile);
            if (!File.Exists(path)) { _log?.Invoke($"buffstacks: no spell icon at {path}"); return null; }

            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.Format != PixelFormats.Bgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            src = new TransformedBitmap(src, new ScaleTransform(
                iconPx / (double)src.PixelWidth, iconPx / (double)src.PixelHeight));

            var bgra = new byte[iconPx * iconPx * 4];
            src.CopyPixels(bgra, iconPx * 4, 0);
            return new BuffStackReader(bgra, iconPx);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"buffstacks: template build failed: {ex.Message}");
            return null;
        }
    }

    // ── capture: PrintWindow into a top-down DIB, crop the band (no System.Drawing) ─────────

    private (byte[] Bgra, int W, int H)? CaptureClientBand(IntPtr hwnd, int clientW, int clientH)
    {
        // Client-area offset inside the window rect (title bar + borders).
        if (!GetWindowRect(hwnd, out var win)) return null;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return null;
        int offX = origin.X - win.Left, offY = origin.Y - win.Top;
        int winW = win.Right - win.Left, winH = win.Bottom - win.Top;
        if (winW <= 0 || winH <= 0) return null;

        int bx0 = offX + (int)(clientW * BandX0), bx1 = offX + (int)(clientW * BandX1);
        int by0 = offY + (int)(clientH * BandY0), by1 = offY + (int)(clientH * BandY1);
        int bw = bx1 - bx0, bh = by1 - by0;
        if (bw <= 0 || bh <= 0 || bx1 > winW || by1 > winH) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFO
        {
            biSize = 40, biWidth = winW, biHeight = -winH, // negative = top-down rows
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        IntPtr dib = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            IntPtr old = SelectObject(memDc, dib);
            bool ok = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
            SelectObject(memDc, old);
            if (!ok) return null;

            var band = new byte[bw * bh * 4];
            for (int y = 0; y < bh; y++)
                Marshal.Copy(bits + ((by0 + y) * winW + bx0) * 4, band, y * bw * 4, bw * 4);
            return (band, bw, bh);
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────────────────────

    private const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>DPI_AWARENESS_CONTEXT_UNAWARE — see the loop-541 note in <see cref="Tick"/>.</summary>
    private static readonly IntPtr DpiAwarenessContextUnaware = new(-1);

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        // BITMAPINFO color table — unused for 32bpp BI_RGB but part of the struct's shape.
        public uint bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
#endif
