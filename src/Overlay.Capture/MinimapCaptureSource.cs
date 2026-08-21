using System.Diagnostics;
using Overlay.Core.Render;
using Overlay.Core.Vision;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace Overlay.Capture;

/// <summary>
/// M31 P1 native <see cref="IMinimapCaptureSource"/>: the orchestrator that ties the pieces
/// together. On <see cref="Start"/> it creates a BGRA-capable D3D11 device, reads the game's
/// <c>game.cfg</c> for calibration (layer 0), probes the display mode to pick the WGC or DXGI
/// backend (<see cref="IFrameProvider"/>), and — per delivered full frame — crops the calibrated
/// minimap ROI on the GPU (<see cref="RoiReadback"/>) and raises <see cref="FrameCaptured"/>,
/// throttled to <c>captureFps</c>. Recalibrates when the captured frame's size changes (resize /
/// mode switch). Self-disables on failure (M31 §6: one log line, no retry storm).
///
/// <para>UNVERIFIED — the whole native path (device, backends, GPU crop, WinRT interop) needs a
/// build + GPU + live game, none available in Cowork. See CLAUDE_CODE_TODO §38-B.</para>
/// </summary>
public sealed class MinimapCaptureSource : IMinimapCaptureSource
{
    private readonly Func<IntPtr> _getGameWindow;
    private readonly string? _gameCfgPathOverride;
    private readonly Action<string>? _log;
    private readonly long _minIntervalMs;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private IFrameProvider? _provider;
    private RoiReadback? _readback;
    private CapturePerfHarness? _perf;

    private GameCfgHudSettings _cfg = GameCfgHudSettings.Empty;
    private IntPtr _hwnd;
    private int _calW, _calH;
    private RenderBounds _roi;
    private bool _flipped;
    private long _lastEmitMs;
    private volatile bool _stopping;

    /// <summary>Optional manual minimap-calibration override (normalized), so the CAPTURE ROI
    /// matches the SAME box the overlay renders structures on. Returns (X, Y, Size) as fractions —
    /// X of width, Y and Size of HEIGHT (a square), mirroring OverlayHost.ResolveMinimapRect's
    /// <c>overlay.minimapCalibration</c> math — or null to use the auto game.cfg/geometric estimate.
    /// This is how enemy detection and the inhibitor/Nexus structure chips stay on ONE map basis:
    /// the user aligns the box once and both follow it.</summary>
    private readonly Func<(double X, double Y, double Size)?>? _manualRoiFraction;

    /// <param name="getGameWindow">Supplies the tracked game HWND (M19 window tracking).</param>
    /// <param name="captureFps">Prefilter cap (M31 §3/§9-2). Clamped 1..60. Default 30.</param>
    /// <param name="gameCfgPathOverride">User-set game.cfg path, used only if auto-discovery fails.</param>
    /// <param name="log">Optional INFO sink (wire to M18 logging).</param>
    /// <param name="manualRoiFraction">Optional normalized manual-calibration ROI (see field doc).</param>
    public MinimapCaptureSource(
        Func<IntPtr> getGameWindow, int captureFps = 30,
        string? gameCfgPathOverride = null, Action<string>? log = null,
        Func<(double X, double Y, double Size)?>? manualRoiFraction = null)
    {
        _getGameWindow = getGameWindow ?? throw new ArgumentNullException(nameof(getGameWindow));
        _gameCfgPathOverride = gameCfgPathOverride;
        _log = log;
        _manualRoiFraction = manualRoiFraction;
        int fps = Math.Clamp(captureFps, 1, 60);
        _minIntervalMs = Math.Max(1, 1000 / fps);
    }

    public event Action<MinimapFrame>? FrameCaptured;

    public bool IsCapturing { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (IsCapturing || _stopping) return;
            try
            {
                _hwnd = _getGameWindow();
                if (_hwnd == IntPtr.Zero) { _log?.Invoke("minimap: no game window; capture not started"); return; }

                CreateDevice();
                _cfg = ReadGameCfg(_hwnd);

                GameDisplayMode mode = WindowProbe.ProbeDisplayMode(_hwnd);
                _readback = new RoiReadback(_device!);
                _perf = new CapturePerfHarness(_log);
                _provider = CreateProvider(mode);
                _provider.Start(OnFrame);

                IsCapturing = true;
                _log?.Invoke($"minimap capture started (mode={mode}, cfg={(_cfg.IsEmpty ? "prior" : "game.cfg")})");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"minimap capture failed to start ({ex.GetType().Name}: {ex.Message}); feature disabled for this session");
                CleanupLocked();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsCapturing && _provider is null) return;
            CleanupLocked();
            _log?.Invoke("minimap capture stopped");
        }
    }

    private IFrameProvider CreateProvider(GameDisplayMode mode)
    {
        // Exclusive fullscreen → DXGI duplication of the game's monitor. Otherwise WGC window
        // capture when supported; DXGI as the fallback. (A WGC "no frames" timeout → DXGI switch
        // is the more robust selector — logged as a follow-up in §38-B.)
        if (mode == GameDisplayMode.ExclusiveFullscreenLikely || !WgcFrameProvider.IsSupported())
        {
            IntPtr monitor = WindowProbe.GetContainingMonitor(_hwnd);
            return new DxgiDuplicationFrameProvider(_device!, monitor);
        }

        _winrtDevice ??= CreateWinRtDevice();
        return new WgcFrameProvider(_hwnd, _winrtDevice, _log);
    }

    private void OnFrame(ID3D11Texture2D texture, long timestampMs)
    {
        if (_stopping) return;
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastEmitMs < _minIntervalMs) return; // fps throttle (identity match is P2, still gated)

            // Pause when the game window is minimized AND in the background (M31 §5 battery note).
            if (WindowProbe.IsMinimized(_hwnd) && !WindowProbe.IsForeground(_hwnd)) return;

            Texture2DDescription desc = texture.Description;
            ResolveRoi((int)desc.Width, (int)desc.Height);
            if (_roi.Width <= 0) return;

            long cropStart = Stopwatch.GetTimestamp();
            MinimapFrame? frame = _readback?.CropAndRead(
                _context!, texture,
                (int)Math.Round(_roi.X), (int)Math.Round(_roi.Y),
                (int)Math.Round(_roi.Width), (int)Math.Round(_roi.Height),
                timestampMs, _flipped);

            if (frame is { } f)
            {
                // §5/§7 harness: time just the crop+readback (the GPU→CPU cost) and the ROI bytes
                // mapped; the downstream FrameCaptured cost belongs to P2, not this measurement.
                double cropMs = Stopwatch.GetElapsedTime(cropStart).TotalMilliseconds;
                _lastEmitMs = now;
                _perf?.Record(cropMs, (long)f.Stride * f.Height, now);
                FrameCaptured?.Invoke(f);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"minimap frame dropped ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Resolve the minimap ROI for the captured frame size. Prefers the user's manual
    /// calibration override (same box the overlay renders structures on — so enemy detection and
    /// the inhibitor/Nexus chips share ONE map basis); otherwise the auto game.cfg/geometric
    /// estimate. Re-checked every frame (cheap) so a mid-game recalibration takes effect; only logs
    /// when the rect materially changes. The captured surface = the full game display area (WGC:
    /// client; DXGI: the monitor the borderless/exclusive game fills), so fractions map directly.</summary>
    private void ResolveRoi(int width, int height)
    {
        RenderBounds roi;
        bool flip;
        string srcLabel;

        var frac = _manualRoiFraction?.Invoke();
        if (frac is { } f && f.Size > 0.0)
        {
            double edge = f.Size * height;
            roi = new RenderBounds(f.X * width, f.Y * height, edge, edge);
            flip = _cfg.FlipMiniMap ?? false; // manual box carries no orientation; flip still from game.cfg
            srcLabel = "manual";
        }
        else
        {
            MinimapCalibration cal = MinimapCalibrator.Compute(width, height, _cfg);
            roi = cal.Rect;
            flip = cal.Flipped;
            srcLabel = cal.Source.ToString();
        }

        // Skip the update+log when nothing material changed (avoids per-frame log spam).
        if (width == _calW && height == _calH && flip == _flipped &&
            Math.Abs(roi.X - _roi.X) < 0.5 && Math.Abs(roi.Y - _roi.Y) < 0.5 &&
            Math.Abs(roi.Width - _roi.Width) < 0.5)
            return;

        _roi = roi;
        _flipped = flip;
        _calW = width;
        _calH = height;
        _log?.Invoke($"minimap calibrated {width}x{height} → roi=({roi.X:F0},{roi.Y:F0},{roi.Width:F0},{roi.Height:F0}) flip={flip} [{srcLabel}]");
    }

    private GameCfgHudSettings ReadGameCfg(IntPtr hwnd)
    {
        string? path = WindowProbe.TryGetGameCfgPath(hwnd) ?? _gameCfgPathOverride;
        return GameCfgReader.Read(path);
    }

    private void CreateDevice()
    {
        // BgraSupport is required for the BGRA staging format + WGC/D2D interop.
        var result = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            Array.Empty<Vortice.Direct3D.FeatureLevel>(), // let the runtime pick the feature level
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        result.CheckError();
        _device = device;
        _context = context;
    }

    private IDirect3DDevice CreateWinRtDevice()
    {
        using var dxgi = _device!.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        return Direct3D11WinRtInterop.CreateDirect3DDevice(dxgi);
    }

    private void CleanupLocked()
    {
        _stopping = true;
        IsCapturing = false;

        try { _provider?.Stop(); } catch { /* ignore */ }
        _provider?.Dispose();
        _readback?.Dispose();
        _winrtDevice?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _provider = null;
        _readback = null;
        _perf = null;
        _winrtDevice = null;
        _context = null;
        _device = null;
        _calW = _calH = 0;
        _roi = default;
        _stopping = false;
    }

    public void Dispose() => Stop();
}
