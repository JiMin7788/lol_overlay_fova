using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Overlay.Capture;

/// <summary>
/// M31 §1 DXGI Desktop Duplication backend (exclusive fullscreen). WGC window capture cannot see
/// exclusive-fullscreen surfaces, so we duplicate the OUTPUT (monitor) containing the game window
/// and crop the minimap ROI off the desktop image. Polls <c>AcquireNextFrame</c> on a background,
/// below-normal-priority thread; recreates the duplication on DXGI access loss (mode switch).
///
/// <para>UNVERIFIED — needs a GPU + a real output; none in Cowork (§38-B). Verify the Vortice
/// DXGI signatures (EnumOutputs / AcquireNextFrame / ResultCode names) at local build.</para>
/// </summary>
internal sealed class DxgiDuplicationFrameProvider : IFrameProvider
{
    private readonly ID3D11Device _device;
    private readonly IntPtr _targetMonitor;
    private readonly Action<string>? _log;

    private IDXGIOutputDuplication? _duplication;
    private Thread? _thread;
    private volatile bool _running;
    private Action<ID3D11Texture2D, long>? _onFrame;

    public DxgiDuplicationFrameProvider(ID3D11Device device, IntPtr targetMonitor, Action<string>? log = null)
    {
        _device = device;
        _targetMonitor = targetMonitor;
        _log = log;
    }

    public bool IsRunning => _running;

    public bool StoppedCleanly { get; private set; } = true;

    public event Action? Died;

    public void Start(Action<ID3D11Texture2D, long> onFrame)
    {
        _onFrame = onFrame;
        _duplication = CreateDuplication();
        _running = true;
        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "minimap-dxgi-duplication",
        };
        _thread.Start();
    }

    /// <summary>Find the DXGI output matching the tracked window's monitor and duplicate it.
    /// Falls back to the first output if no exact monitor match is found.</summary>
    private IDXGIOutputDuplication CreateDuplication()
    {
        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();

        IDXGIOutput? chosen = null;
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? output).Success && output is not null; i++)
        {
            if (output.Description.Monitor == _targetMonitor)
            {
                chosen?.Dispose(); // release the first-output fallback we no longer need
                chosen = output;
                break;
            }
            if (chosen is null) chosen = output; // remember the first as a fallback
            else output.Dispose();
        }

        if (chosen is null)
            throw new InvalidOperationException("no DXGI output found for the game monitor");

        try
        {
            using IDXGIOutput1 output1 = chosen.QueryInterface<IDXGIOutput1>();
            return output1.DuplicateOutput(_device);
        }
        finally
        {
            chosen.Dispose();
        }
    }

    private void PollLoop()
    {
        bool died = false;
        while (_running)
        {
            IDXGIOutputDuplication? dup = _duplication;
            if (dup is null) break;

            var result = dup.AcquireNextFrame(500, out OutduplFrameInfo _, out IDXGIResource? resource);
            if (result.Failure || resource is null)
            {
                if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue;    // no update this interval
                if (result == Vortice.DXGI.ResultCode.AccessLost && Recreate()) continue; // mode switch
                died = _running; // unrecoverable (or recreate failed) → self-disable; a Stop() request is not a death
                break;
            }

            try
            {
                using ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>();
                _onFrame?.Invoke(texture, Environment.TickCount64);
            }
            finally
            {
                resource.Dispose();
                try { dup.ReleaseFrame(); } catch { /* frame already released / lost */ }
            }
        }
        _running = false;
        if (died) Died?.Invoke();
    }

    private bool Recreate()
    {
        try
        {
            _duplication?.Dispose();
            _duplication = CreateDuplication();
            return true;
        }
        catch
        {
            return false; // give up rather than spin (M31 §6: no retry storm)
        }
    }

    public void Stop()
    {
        _running = false;
        if (!StoppedCleanly) return; // a prior Stop already timed out; the leak stands

        // (loop 516) Join-then-dispose, and NEVER dispose on a timed-out join: the poll thread can
        // legitimately be up to ~500ms inside AcquireNextFrame plus one _onFrame (GPU crop), and
        // the old Join(1000)-then-dispose freed the duplication/device objects under it on a slow
        // frame — a native access violation, not a catchable exception. 5s covers every legitimate
        // wait; past that the thread is wedged in the driver and leaking beats crashing.
        bool joined = true;
        try { joined = _thread?.Join(5000) ?? true; } catch { /* ignore */ }
        _thread = null;

        if (joined)
        {
            _duplication?.Dispose();
            _duplication = null;
        }
        else
        {
            StoppedCleanly = false;
            _log?.Invoke("dxgi: poll thread did not exit within 5s — leaking the duplication " +
                         "(and the shared device, see orchestrator) rather than disposing under a live thread");
        }
    }

    public void Dispose() => Stop();
}
