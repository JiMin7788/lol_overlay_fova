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
/// mode switch). Self-disables on failure (M31 §6: one log line, no retry storm). While capturing,
/// re-probes the window once per second and restarts on a settled mode/monitor/DPI change, and
/// falls back from WGC to DXGI when WGC delivers no frame (lolF03 R8/R9).
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

    // lolF03 R8/R9: 1 s window probe → backend re-choice on mode/monitor/DPI change and the WGC
    // no-frame → DXGI fallback. The policies are guarded by _policyLock, never _gate, so neither the
    // probe callback nor the frame callback ever waits on a restart.
    private const int ProbeIntervalMs = 1000;
    private readonly object _policyLock = new();
    private readonly CaptureRestartPolicy _restartPolicy = new();
    private readonly WgcNoFramePolicy _noFramePolicy = new();
    private bool _watchWgc;               // under _policyLock: this session is WGC and not yet fallen back
    private Timer? _probeTimer;           // under _gate
    private int _restarting;              // Interlocked: 1 while one restart worker is queued/running
    private volatile bool _stopped;       // set by public Stop(), cleared by public Start()
    private bool _forceDxgi;              // under _gate: WGC fell back this game; cleared by public Stop()

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
            _stopped = false;
            if (IsCapturing || _stopping) return;
            // A backend that died mid-session (OnProviderDied) leaves IsCapturing false with live
            // resources — release them before building new ones, or a restart leaks a full set.
            if (_provider is not null) CleanupLocked();
            try
            {
                if (!StartLocked()) _log?.Invoke("minimap: no game window; capture not started");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"minimap capture failed to start ({ex.GetType().Name}: {ex.Message}); feature disabled for this session");
                CleanupLocked();
            }
        }
    }

    /// <summary>Probe the window, choose the backend and start it. False = no game window. Throws on
    /// failure; the caller logs and calls <see cref="CleanupLocked"/>. Caller holds <see cref="_gate"/>.</summary>
    private bool StartLocked()
    {
        _hwnd = _getGameWindow();
        if (_hwnd == IntPtr.Zero) return false;

        CreateDevice();
        _cfg = ReadGameCfg(_hwnd);

        GameDisplayMode mode = WindowProbe.ProbeDisplayMode(_hwnd);
        _readback = new RoiReadback(_device!);
        _perf = new CapturePerfHarness(_log);
        _provider = CreateProvider(mode);
        _provider.Died += OnProviderDied;

        // Before _provider.Start, so a frame that arrives at once is not wiped by _noFramePolicy.Start.
        long now = Environment.TickCount64;
        lock (_policyLock)
        {
            _restartPolicy.Begin(ProbeWindow(_hwnd), now);
            _watchWgc = _provider is WgcFrameProvider;
            if (_watchWgc) _noFramePolicy.Start(now);
        }
        _provider.Start(OnFrame);

        IsCapturing = true;
        StartProbeTimerLocked();
        _log?.Invoke($"minimap capture started (mode={mode}, cfg={(_cfg.IsEmpty ? "prior" : "game.cfg")})");
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _forceDxgi = false;
            if (!IsCapturing && _provider is null && _probeTimer is null) return;
            CleanupLocked();
            _log?.Invoke("minimap capture stopped");
        }
    }

    private void StartProbeTimerLocked()
        => _probeTimer ??= new Timer(OnProbeTick, null, ProbeIntervalMs, ProbeIntervalMs);

    private static CaptureProbe ProbeWindow(IntPtr hwnd)
        => hwnd == IntPtr.Zero
            ? default
            : new CaptureProbe(WindowProbe.ProbeDisplayMode(hwnd).ToString(),
                WindowProbe.GetMonitorOrZero(hwnd), WindowProbe.GetDpi(hwnd));

    /// <summary>Timer callback, once per second. Probes the window outside <see cref="_gate"/> and,
    /// when a policy says so, queues the single restart worker; it never restarts inline.</summary>
    private void OnProbeTick(object? state)
    {
        if (_stopped || Volatile.Read(ref _restarting) != 0) return;
        try
        {
            IntPtr hwnd = _hwnd;
            long now = Environment.TickCount64;
            CaptureProbe sample = ProbeWindow(hwnd);
            bool minimized = hwnd != IntPtr.Zero && WindowProbe.IsMinimized(hwnd);

            CaptureProbe old;
            bool modeChanged, fallback = false;
            lock (_policyLock)
            {
                old = _restartPolicy.Baseline;
                modeChanged = _restartPolicy.Observe(sample, now);
                if (_watchWgc)
                {
                    _noFramePolicy.SetMinimized(minimized, now);
                    fallback = !modeChanged && _noFramePolicy.ShouldFallback(now);
                    if (fallback) _watchWgc = false;
                }
            }
            if (!modeChanged && !fallback) return;
            if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0) return;

            _log?.Invoke(modeChanged
                ? $"capture: display mode changed {old} -> {sample} — restarting capture"
                : "capture: WGC delivered no frame in 3000 ms — falling back to DXGI duplication");
            CaptureProbe? attempted = modeChanged ? sample : null;
            ThreadPool.QueueUserWorkItem(_ => RestartWorker(attempted));
        }
        catch
        {
            // AI-NOTE: an exception escaping a timer callback kills the process; a failed window
            // query only skips this tick (the next one probes again).
        }
    }

    /// <summary>The one queued restart (mode change: <paramref name="attempted"/> = new probe;
    /// WGC no-frame fallback: null). Serialised with Start/Stop by <see cref="_gate"/>.</summary>
    private void RestartWorker(CaptureProbe? attempted)
    {
        try
        {
            lock (_gate)
            {
                if (_stopped) return;
                if (attempted is null) _forceDxgi = true;
                // R9: a mode restart keeps the fallback only while the new mode is still a WGC mode.
                else if (attempted.Value.Mode == nameof(GameDisplayMode.ExclusiveFullscreenLikely)) _forceDxgi = false;

                string? error = null;
                try
                {
                    CleanupLocked();
                    if (!StartLocked()) error = "no game window";
                }
                catch (Exception ex) { error = ex.Message; }
                if (error is null) return;

                _log?.Invoke($"capture: restart failed — {error}");
                CleanupLocked();
                if (attempted is { } a)
                    lock (_policyLock) _restartPolicy.RestartFailed(a, Environment.TickCount64);
                // AI-NOTE: keep probing with the failed probe as baseline, so the next real change
                // retries instead of capture staying off until the next GAME.CONNECTED.
                StartProbeTimerLocked();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _restarting, 0);
        }
    }

    private IFrameProvider CreateProvider(GameDisplayMode mode)
    {
        // Exclusive fullscreen → DXGI duplication of the game's monitor. Otherwise WGC window
        // capture when supported; DXGI as the fallback. A WGC session that delivers no frame
        // (lolF03 R9) sets _forceDxgi and restarts here.
        if (_forceDxgi || mode == GameDisplayMode.ExclusiveFullscreenLikely || !WgcFrameProvider.IsSupported())
        {
            IntPtr monitor = WindowProbe.GetContainingMonitor(_hwnd);
            return new DxgiDuplicationFrameProvider(_device!, monitor, _log);
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
            lock (_policyLock) _noFramePolicy.OnFrame(now);
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
            // (loop 516) Rate-limited: on a removed device every delivered frame throws, and the
            // per-frame version of this line wrote up to 30 lines/s indefinitely — exactly the
            // retry/log storm M31 §6 forbids. One line per window, with the drop count.
            long now = Environment.TickCount64;
            _dropCount++;
            if (now - _lastDropLogMs >= DropLogWindowMs)
            {
                _log?.Invoke($"minimap frame dropped ×{_dropCount} (last: {ex.GetType().Name}: {ex.Message})");
                _dropCount = 0;
                _lastDropLogMs = now;
            }
        }
    }

    private int _dropCount;
    private long _lastDropLogMs;
    private const long DropLogWindowMs = 5000;

    /// <summary>Objects deliberately kept alive forever after a timed-out capture-thread join —
    /// see the leak branch in <see cref="CleanupLocked"/>. Bounded by how often that branch can
    /// run (once per wedged session), so this is a pathological-path safety root, not growth.</summary>
    private static readonly List<object> LeakedOnPurpose = new();

    /// <summary>The backend self-disabled (device removed, window closed, unrecoverable DXGI
    /// error). Runs on the PROVIDER'S OWN thread, so no teardown happens here — the DXGI poll
    /// thread would be joining itself inside Stop(). This only makes the death observable
    /// (IsCapturing goes false, per the IMinimapCaptureSource contract, instead of lying) and logs
    /// once; the owner's next Stop()/Start() performs the actual cleanup.</summary>
    private void OnProviderDied()
    {
        IsCapturing = false;
        _log?.Invoke("minimap: capture backend self-disabled (device lost / game window closed) — capture off until restarted");
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

        if (_probeTimer is not null)
        {
            // AI-NOTE: wait for an in-flight probe tick, so none runs after Stop() returns. Safe
            // under _gate: the tick never takes _gate, and this never runs on the timer thread.
            using var done = new ManualResetEvent(false);
            if (_probeTimer.Dispose(done)) done.WaitOne();
            _probeTimer = null;
        }
        lock (_policyLock) _watchWgc = false;

        bool clean = true;
        if (_provider is not null)
        {
            _provider.Died -= OnProviderDied;
            try { _provider.Stop(); } catch { /* ignore */ }
            clean = _provider.StoppedCleanly;
            if (clean) _provider.Dispose();
        }

        if (clean)
        {
            _readback?.Dispose();
            _winrtDevice?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
        else
        {
            // (loop 516) The DXGI poll thread outlived its join timeout, so it may still be inside
            // CopySubresourceRegion/Map on these objects. Disposing them under it is a native
            // access violation; leaking is the safe failure. Rooting them statically matters:
            // merely dropping the references would let their FINALIZERS release the native
            // pointers later — the same AV, just delayed.
            foreach (var leaked in new object?[] { _provider, _readback, _winrtDevice, _context, _device })
                if (leaked is not null) LeakedOnPurpose.Add(leaked);
            _log?.Invoke("minimap: capture thread did not exit — leaking the D3D device/staging objects instead of disposing under a live thread");
        }

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
