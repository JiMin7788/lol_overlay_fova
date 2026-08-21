using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Overlay.Capture;

/// <summary>
/// M31 §1 Windows Graphics Capture backend (windowed / borderless). Captures the game HWND via a
/// free-threaded <see cref="Direct3D11CaptureFramePool"/> — frame-on-change delivery, so a static
/// screen costs ~0. Cursor capture and the yellow capture border are disabled (Win10 20H1+ /
/// Win11). Recreates the pool on window resize (alt-enter / DPI change).
///
/// <para>UNVERIFIED — WGC needs a live desktop + GPU; none in Cowork (§38-B). Verify the WinRT
/// call surface at local build.</para>
/// </summary>
internal sealed class WgcFrameProvider : IFrameProvider
{
    private const DirectXPixelFormat Bgra = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int BufferCount = 2;

    private readonly IntPtr _hwnd;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Action<string>? _log;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Action<ID3D11Texture2D, long>? _onFrame;
    private SizeInt32 _lastSize;
    private readonly object _gate = new();
    private int _arrivals;
    private bool _loggedError;
    private int _errorStreak;

    /// <summary>(loop 516) Consecutive FrameArrived failures before declaring the backend dead. A
    /// removed D3D device makes EVERY frame throw; the once-only error log kept that invisible
    /// while the session burned a callback per frame forever. One transient failure never trips it.</summary>
    private const int MaxErrorStreak = 30;

    public WgcFrameProvider(IntPtr hwnd, IDirect3DDevice winrtDevice, Action<string>? log = null)
    {
        _hwnd = hwnd;
        _winrtDevice = winrtDevice;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Always true here: <see cref="Stop"/> blocks on <see cref="_gate"/> until the
    /// in-flight callback finishes, so disposal never races the delivery thread.</summary>
    public bool StoppedCleanly => true;

    public event Action? Died;

    /// <summary>Whether WGC is available on this OS at all (Win10 1903+). Cheap static probe the
    /// orchestrator can use before choosing this backend.</summary>
    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    public void Start(Action<ID3D11Texture2D, long> onFrame)
    {
        _onFrame = onFrame;
        _item = Direct3D11WinRtInterop.CreateItemForWindow(_hwnd);
        _lastSize = _item.Size;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, Bgra, BufferCount, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        TrySet(() => _session.IsCursorCaptureEnabled = false); // Win10 20H1+
        TrySet(() => _session.IsBorderRequired = false);        // Win11 (removes the capture outline)

        _item.Closed += (_, _) => { Stop(); Died?.Invoke(); }; // game window closed → observable death
        _session.StartCapture();
        IsRunning = true;
        _log?.Invoke($"WGC started: item {_item.Size.Width}x{_item.Size.Height}, hwnd={_hwnd} (awaiting FrameArrived…)");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            lock (_gate)
            {
                if (!IsRunning) return;
                Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null) { _log?.Invoke("WGC FrameArrived: TryGetNextFrame returned null"); return; }

                // Window resize → recreate the pool at the new size (WGC requirement). (loop 516)
                // The frame from the OLD pool must be disposed BEFORE the recreate and this delivery
                // dropped: the old code recreated the pool and then read the still-live frame's
                // surface — a discarded buffer — and for that frame the texture carried the old pool
                // size while the content was the new size, so the ROI calibrated against the wrong
                // dimensions. The dispose-then-recreate ordering lives in the finally below; one
                // dropped frame per resize is invisible at 30fps.
                bool resized = false;
                try
                {
                    if (frame.ContentSize.Width != _lastSize.Width || frame.ContentSize.Height != _lastSize.Height)
                    {
                        _lastSize = frame.ContentSize;
                        resized = true;
                        return;
                    }

                    long ts = frame.SystemRelativeTime.Ticks / TimeSpan.TicksPerMillisecond;
                    using ID3D11Texture2D texture = Direct3D11WinRtInterop.GetTexture2D(frame.Surface);

                    if (++_arrivals == 1)
                        _log?.Invoke($"WGC first frame delivered ({frame.ContentSize.Width}x{frame.ContentSize.Height}) → forwarding to detector");

                    _onFrame?.Invoke(texture, ts);
                    _errorStreak = 0;
                }
                finally
                {
                    frame.Dispose();
                    if (resized) sender.Recreate(_winrtDevice, Bgra, BufferCount, _lastSize);
                }
            }
        }
        catch (Exception ex)
        {
            // The interop cast in GetTexture2D / the WGC surface access is the highest-risk spot;
            // WinRT would otherwise swallow this silently. Log once so it is visible.
            if (!_loggedError)
            {
                _loggedError = true;
                _log?.Invoke($"WGC FrameArrived ERROR (further suppressed): {ex.GetType().Name}: {ex.Message}");
            }
            // (loop 516) A removed device fails every frame forever — stop the session and make the
            // death observable instead of burning a silent callback per frame for the rest of the game.
            if (++_errorStreak >= MaxErrorStreak && IsRunning)
            {
                _log?.Invoke($"WGC: {MaxErrorStreak} consecutive frame failures — backend self-disabling (device lost?)");
                Stop();
                Died?.Invoke();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning && _framePool is null) return;
            IsRunning = false;

            if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
            _item = null;
        }
    }

    public void Dispose() => Stop();

    // Some session properties throw on older builds; ignore rather than fail the whole capture.
    private static void TrySet(Action set)
    {
        try { set(); }
        catch { /* property not present on this OS build — non-fatal */ }
    }
}
