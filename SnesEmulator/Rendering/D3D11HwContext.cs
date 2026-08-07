using System;
using System.Runtime.InteropServices;
using SnesEmulator.Emulation;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SnesEmulator.Rendering;

// Offscreen D3D11 presentation context for hardware-rendering libretro cores
// (LRPS2). The core renders with this device on its own thread during
// retro_run; the frontend binds the target beforehand and reads the presented
// frame back afterwards, feeding the ordinary RGBA pipeline.
public sealed class D3D11HwContext : IHwRenderContext, IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly FeatureLevel featureLevel;
    private readonly IntPtr d3dCompile;

    // GET_HW_RENDER_INTERFACE hands the core a POINTER to the interface
    // struct (libretro.h: "The frontend will store a pointer to the
    // interface at the address provided here"), and the core keeps that
    // pointer for its whole session. The struct therefore lives in
    // unmanaged memory that never moves, allocated once with the context.
    private readonly IntPtr interfacePtr;

    private ID3D11Texture2D? renderTarget;
    private ID3D11Texture2D? staging;
    private ID3D11RenderTargetView? renderTargetView;
    private int rtWidth;
    private int rtHeight;

    public int ContextType => Libretro.HwContextD3D11;

    private const int DriverTypeHardware = 1;
    private const uint D3D11SdkVersion = 7;

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    private D3D11HwContext(ID3D11Device device, ID3D11DeviceContext context,
        FeatureLevel featureLevel, IntPtr d3dCompile)
    {
        this.device = device;
        this.context = context;
        this.featureLevel = featureLevel;
        this.d3dCompile = d3dCompile;

        interfacePtr = Marshal.AllocHGlobal(Marshal.SizeOf<RetroHwRenderInterfaceD3D11>());
        Marshal.StructureToPtr(new RetroHwRenderInterfaceD3D11
        {
            InterfaceType = Libretro.HwRenderInterfaceD3D11,
            InterfaceVersion = Libretro.HwRenderInterfaceD3D11Version,
            Handle = IntPtr.Zero,
            Device = device.NativePointer,
            Context = context.NativePointer,
            FeatureLevel = (int)featureLevel,
            D3DCompile = d3dCompile,
        }, interfacePtr, false);
    }

    public static D3D11HwContext? TryCreate()
    {
        IntPtr d3dCompile = IntPtr.Zero;
        if (NativeLibrary.TryLoad("d3dcompiler_47.dll", out var compiler) &&
            NativeLibrary.TryGetExport(compiler, "D3DCompile", out var export))
        {
            d3dCompile = export;
        }

        if (d3dCompile == IntPtr.Zero)
        {
            return null;
        }

        var hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, 0,
            IntPtr.Zero, 0, D3D11SdkVersion, out var devicePtr, out var level, out var contextPtr);
        if (hr < 0 || devicePtr == IntPtr.Zero || contextPtr == IntPtr.Zero)
        {
            return null;
        }

        return new D3D11HwContext(new ID3D11Device(devicePtr), new ID3D11DeviceContext(contextPtr),
            (FeatureLevel)level, d3dCompile);
    }

    public bool FillInterface(IntPtr data)
    {
        Marshal.WriteIntPtr(data, interfacePtr);
        return true;
    }

    public void BeginFrame(int width, int height)
    {
        EnsureTarget(width, height);
        if (renderTargetView == null)
        {
            return;
        }

        context.OMSetRenderTargets(new[] { renderTargetView }, null);
        context.RSSetViewports(new[] { new Viewport(0, 0, width, height) });
    }

    public bool TryReadFrame(out byte[] rgba, out int width, out int height)
    {
        rgba = Array.Empty<byte>();
        width = rtWidth;
        height = rtHeight;

        if (renderTarget == null || staging == null || rtWidth == 0)
        {
            return false;
        }

        context.CopyResource(renderTarget, staging);
        var box = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        if (box.DataPointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            rgba = new byte[rtWidth * rtHeight * 4];
            unsafe
            {
                var src = (uint*)box.DataPointer;
                var rowPixels = box.RowPitch / 4;
                fixed (byte* dst = rgba)
                {
                    var outPix = (uint*)dst;
                    for (var y = 0; y < rtHeight; y++)
                    {
                        for (var x = 0; x < rtWidth; x++)
                        {
                            outPix[y * rtWidth + x] = src[y * rowPixels + x] | 0xFF000000;
                        }
                    }
                }
            }

            return true;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private void EnsureTarget(int width, int height)
    {
        if (renderTarget != null && rtWidth == width && rtHeight == height)
        {
            return;
        }

        renderTargetView?.Dispose();
        renderTarget?.Dispose();
        staging?.Dispose();
        renderTargetView = null;
        renderTarget = null;
        staging = null;

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8X8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        renderTarget = device.CreateTexture2D(desc);

        var stagingDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8X8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        };
        staging = device.CreateTexture2D(stagingDesc);

        renderTargetView = device.CreateRenderTargetView(renderTarget, null);
        rtWidth = width;
        rtHeight = height;
    }

    public void Dispose()
    {
        renderTargetView?.Dispose();
        renderTarget?.Dispose();
        staging?.Dispose();
        context.Dispose();
        device.Dispose();
        Marshal.FreeHGlobal(interfacePtr);
    }
}
