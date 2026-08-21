using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Overlay.Capture;

/// <summary>
/// M31 P1 WGC ⇄ D3D11 interop bridge — the **highest-risk** native surface in the module
/// (WinRT/CsWinRT COM plumbing, unit-test-blind, "highest error cost" per the round staffing).
/// Three canonical operations:
/// <list type="number">
/// <item>Wrap a Vortice D3D11/DXGI device as a WinRT <see cref="IDirect3DDevice"/> (WGC's
/// <c>Direct3D11CaptureFramePool.Create</c> needs one).</item>
/// <item>Create a <see cref="GraphicsCaptureItem"/> for a game HWND via the COM interop factory
/// (<c>IGraphicsCaptureItemInterop</c> — the only way to capture a specific window).</item>
/// <item>Get the backing <see cref="ID3D11Texture2D"/> out of a captured WGC frame's
/// <see cref="IDirect3DSurface"/> (<c>IDirect3DDxgiInterfaceAccess</c>).</item>
/// </list>
///
/// <para><b>UNVERIFIED — VERIFY AT LOCAL BUILD (CLAUDE_CODE_TODO §38-B):</b> the exact CsWinRT
/// helper calls (<see cref="ActivationFactory"/>, <see cref="MarshalInspectable{T}"/>,
/// projected-interface casts) shift between CsWinRT versions resolved by the
/// <c>net8.0-windows10.0.19041.0</c> TFM. This follows the Microsoft Win32CaptureSample pattern;
/// adjust helper names if the compiler disagrees. GUIDs below are the fixed native IIDs.</para>
/// </summary>
internal static class Direct3D11WinRtInterop
{
    // Native IIDs (stable, from the Windows SDK headers).
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>COM interop factory interface on the GraphicsCaptureItem activation factory —
    /// lets us make an item for a specific HWND (or monitor).</summary>
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    /// <summary>Access the DXGI/D3D11 interface underlying a WinRT surface.</summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    /// <summary>Wrap a Vortice DXGI device as a WinRT <see cref="IDirect3DDevice"/> for WGC.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
    {
        uint hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pUnknown);
        if (hr != 0)
            Marshal.ThrowExceptionForHR((int)hr);
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(pUnknown);
        }
        finally
        {
            Marshal.Release(pUnknown);
        }
    }

    /// <summary>Create a capture item for a specific game window.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = ActivationFactory
            .Get("Windows.Graphics.Capture.GraphicsCaptureItem")
            .AsInterface<IGraphicsCaptureItemInterop>();

        Guid iid = IID_IGraphicsCaptureItem;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    /// <summary>Get the D3D11 texture backing a captured frame's surface. The returned texture
    /// owns a reference (GetInterface AddRef'd) — dispose it after the ROI copy.</summary>
    public static ID3D11Texture2D GetTexture2D(IDirect3DSurface surface)
    {
        // A plain C# cast on a CsWinRT-projected surface does NOT perform a COM QueryInterface
        // (it throws InvalidCastException: 'WinRT.IInspectable' → interop iface). WinRT's As<T>()
        // extension (WinRT.CastExtensions, `using WinRT`) does the real QI to the [ComImport] iface.
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = IID_ID3D11Texture2D;
        IntPtr texPtr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(texPtr);
    }
}
