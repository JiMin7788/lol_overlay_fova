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

    private IDXGIOutputDuplication? _duplication;
    private Thread? _thread;
    private volatile bool _running;
    private Action<ID3D11Texture2D, long>? _onFrame;

    public DxgiDuplicationFrameProvider(ID3D11Device device, IntPtr targetMonitor)
    {
        _device = device;
        _targetMonitor = targetMonitor;
    }

    public bool IsRunning => _running;

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
        while (_running)
        {
            IDXGIOutputDuplication? dup = _duplication;
            if (dup is null) break;

            var result = dup.AcquireNextFrame(500, out OutduplFrameInfo _, out IDXGIResource? resource);
            if (result.Failure || resource is null)
            {
                if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue;    // no update this interval
                if (result == Vortice.DXGI.ResultCode.AccessLost) { Recreate(); continue; } // mode switch
                break; // unrecoverable → self-disable (orchestrator sees IsRunning=false)
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
    }

    private void Recreate()
    {
        try
        {
            _duplication?.Dispose();
            _duplication = CreateDuplication();
        }
        catch
        {
            _running = false; // give up rather than spin (M31 §6: no retry storm)
        }
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(1000); } catch { /* ignore */ }
        _thread = null;
        _duplication?.Dispose();
        _duplication = null;
    }

    public void Dispose() => Stop();
}
