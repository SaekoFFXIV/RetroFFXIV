using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using VorticeMath = Vortice.Mathematics;

namespace SnesEmulator.Rendering;

// Depth-integrated world screen: draws the video quads inside the game's
// DX11 frame with a read-only depth test against the scene depth buffer, so
// characters and geometry occlude the screens.
//
// Patch-resilience by construction:
//  - Present (vtable 8) and OMSetRenderTargets (vtable 30) are COM ABI —
//    stable across game patches. Found via a throwaway dummy device.
//  - The view-projection matrix is reconstructed every frame on the game
//    thread from IGameGui.WorldToScreen projections (DLT least squares) —
//    no game signatures, no struct offsets.
//  - Reverse-Z vs standard depth is auto-detected by reading one sky pixel.
public sealed class DxWorldRenderer : IDisposable
{
    private const int PresentVtableIndex = 8;
    private const int OMSetRenderTargetsVtableIndex = 30;

    // DXGI_FORMAT values used numerically to dodge binding-name roulette.
    private const int DxgiFormatD32Float = 40;
    private const int DxgiFormatD24UNormS8UInt = 45;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long PresentDelegate(IntPtr swapChain, uint syncInterval, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(IntPtr context, uint numViews, IntPtr rtvs, IntPtr dsv);

    // ID3D11View::GetResource — vtable slot 4 (after IUnknown + GetDevice).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long GetResourceDelegate(IntPtr self, out IntPtr resource);

    // IDXGISwapChain::GetBuffer — vtable slot 9.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long GetBufferDelegate(IntPtr self, uint buffer, ref Guid riid, out IntPtr surface);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    public struct ScreenQuad
    {
        public string Id;
        public Vector3 Center;
        public float HalfWidth;
        public float HalfHeight;
    }

    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;

    private Hook<PresentDelegate>? presentHook;
    private Hook<OMSetRenderTargetsDelegate>? omHook;

    // Data handed over from the game thread (locked).
    private readonly object frameLock = new();
    private Matrix4x4 viewProj = Matrix4x4.Identity;
    private bool hasViewProj;
    private List<ScreenQuad> quads = new();
    private readonly Dictionary<string, byte[]> pendingFrames = new();
    private readonly Dictionary<string, (int W, int H)> pendingSizes = new();

    // DX state (render thread only).
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11VertexShader? vs;
    private ID3D11PixelShader? ps;
    private ID3D11InputLayout? layout;
    private ID3D11Buffer? vertexBuffer;
    private ID3D11Buffer? indexBuffer;
    private ID3D11Buffer? constantBuffer;
    private ID3D11BlendState? blendState;
    private ID3D11DepthStencilState? depthState;
    private ID3D11DepthStencilState? depthStateReverse;
    private ID3D11RasterizerState? rasterState;
    private ID3D11SamplerState? samplerState;
    private ID3D11RenderTargetView? backbufferView;
    private int backbufferW, backbufferH;

    private readonly Dictionary<string, QuadTexture> textures = new();
    private readonly Dictionary<IntPtr, bool> dsvFullSize = new();
    private IntPtr capturedDsv;
    private ID3D11DepthStencilView? capturedDsvWrapper;
    private bool? reverseZ;
    private bool resourcesReady;
    private bool failed;
    private int loggedErrors;

    private sealed class QuadTexture : IDisposable
    {
        public ID3D11Texture2D? Texture;
        public ID3D11ShaderResourceView? Srv;
        public int W, H;

        public void Dispose()
        {
            Srv?.Dispose();
            Texture?.Dispose();
            Srv = null;
            Texture = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector2 Uv;
    }

    public bool Enabled => presentHook?.IsEnabled == true;
    public bool Failed => failed;

    public DxWorldRenderer(IGameInteropProvider interop, IPluginLog log)
    {
        this.interop = interop;
        this.log = log;
    }

    // --- lifecycle -----------------------------------------------------

    public void Enable()
    {
        if (failed)
            return;

        if (presentHook != null)
        {
            if (!presentHook.IsEnabled)
            {
                presentHook.Enable();
                omHook?.Enable();
            }
            return;
        }

        try
        {
            var (presentAddr, omAddr) = FindVtableAddresses();
            presentHook = interop.HookFromAddress<PresentDelegate>(presentAddr, PresentDetour);
            omHook = interop.HookFromAddress<OMSetRenderTargetsDelegate>(omAddr, OmDetour);
            presentHook.Enable();
            omHook.Enable();
            log.Information("[DxScreen] hooks installed (Present + OMSetRenderTargets)");
        }
        catch (Exception ex)
        {
            failed = true;
            log.Error($"[DxScreen] hook install failed: {ex.Message}");
        }
    }

    public void Disable()
    {
        presentHook?.Disable();
        omHook?.Disable();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeModeDesc
    {
        public int Width;
        public int Height;
        public int RefreshNum;
        public int RefreshDen;
        public int Format;
        public int ScanlineOrdering;
        public int Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSwapChainDesc
    {
        public NativeModeDesc BufferDesc;
        public int SampleCount;
        public int SampleQuality;
        public int BufferUsage;
        public int BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public int SwapEffect;
        public int Flags;
    }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDeviceAndSwapChain(
        IntPtr adapter, int driverType, IntPtr software, int flags,
        IntPtr featureLevels, int featureLevelCount, int sdkVersion,
        ref NativeSwapChainDesc desc, out IntPtr swapChain, out IntPtr device,
        out IntPtr featureLevel, out IntPtr context);

    private static (IntPtr Present, IntPtr OmSetRenderTargets) FindVtableAddresses()
    {
        // The probe swap chain only needs *some* HWND to read vtables from —
        // reuse the game's main window instead of creating one (window creation
        // from the render thread failed inside the game process).
        var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        var createdHwnd = false;
        if (hwnd == IntPtr.Zero)
        {
            hwnd = CreateWindowExW(0, "STATIC", "dxprobe", 0, 0, 0, 8, 8, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            createdHwnd = hwnd != IntPtr.Zero;
        }

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"probe window failed: win32 error {Marshal.GetLastWin32Error()}");

        try
        {
            var desc = new NativeSwapChainDesc
            {
                BufferDesc = new NativeModeDesc { Width = 8, Height = 8, RefreshNum = 60, RefreshDen = 1, Format = 28 },
                SampleCount = 1,
                BufferUsage = 0x20, // DXGI_USAGE_RENDER_TARGET_OUTPUT
                BufferCount = 1,
                OutputWindow = hwnd,
                Windowed = 1,
            };

            var hr = D3D11CreateDeviceAndSwapChain(
                IntPtr.Zero, 1 /* D3D_DRIVER_TYPE_HARDWARE */, IntPtr.Zero, 0,
                IntPtr.Zero, 0, 7, ref desc,
                out var chainPtr, out var devicePtr, out _, out var ctxPtr);
            if (hr < 0)
                throw new InvalidOperationException($"D3D11CreateDeviceAndSwapChain failed: 0x{hr:X8}");

            try
            {
                var swapVtable = Marshal.ReadIntPtr(chainPtr);
                var present = Marshal.ReadIntPtr(swapVtable + PresentVtableIndex * IntPtr.Size);

                var ctxVtable = Marshal.ReadIntPtr(ctxPtr);
                var om = Marshal.ReadIntPtr(ctxVtable + OMSetRenderTargetsVtableIndex * IntPtr.Size);

                return (present, om);
            }
            finally
            {
                Marshal.Release(chainPtr);
                Marshal.Release(devicePtr);
                Marshal.Release(ctxPtr);
            }
        }
        finally
        {
            if (createdHwnd)
                DestroyWindow(hwnd);
        }
    }

    // --- game-thread API ------------------------------------------------

    public void SubmitFrame(Matrix4x4? viewProj, List<ScreenQuad> quads,
        Dictionary<string, (byte[] Rgba, int W, int H)> frames)
    {
        lock (frameLock)
        {
            hasViewProj = viewProj.HasValue;
            if (viewProj.HasValue)
                this.viewProj = viewProj.Value;
            this.quads = quads;
            pendingFrames.Clear();
            pendingSizes.Clear();
            foreach (var (id, (rgba, w, h)) in frames)
            {
                pendingFrames[id] = rgba;
                pendingSizes[id] = (w, h);
            }
        }
    }

    // --- hooks (render thread) ------------------------------------------

    private void OmDetour(IntPtr ctx, uint numViews, IntPtr rtvs, IntPtr dsv)
    {
        omHook!.Original(ctx, numViews, rtvs, dsv);

        if (dsv == IntPtr.Zero || numViews == 0 || rtvs == IntPtr.Zero)
            return;

        // Capture the depth view that is bound together with a backbuffer-
        // sized target — that is the scene depth.
        if (!dsvFullSize.TryGetValue(dsv, out var full))
        {
            full = QueryDsvFullSize(dsv);
            dsvFullSize[dsv] = full;
        }

        if (full)
            capturedDsv = dsv;
    }

    private bool QueryDsvFullSize(IntPtr dsvPtr)
    {
        try
        {
            var resPtr = ViewGetResource(dsvPtr);
            if (resPtr == IntPtr.Zero)
                return false;
            using var resource = new ID3D11Resource(resPtr); // owns the GetResource reference
            var texture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;
            texture.Dispose();

            int bbW, bbH;
            lock (frameLock)
            {
                bbW = backbufferW;
                bbH = backbufferH;
            }

            if (bbW > 0)
                return desc.Width == bbW && desc.Height == bbH;
            return desc.Width >= 1280;
        }
        catch
        {
            return false;
        }
    }

    // ID3D11View::GetResource via raw vtable (slot 4) — returns +1 reference.
    private static IntPtr ViewGetResource(IntPtr viewPtr)
    {
        var vtbl = Marshal.ReadIntPtr(viewPtr);
        var fn = Marshal.ReadIntPtr(vtbl + 4 * IntPtr.Size);
        var d = Marshal.GetDelegateForFunctionPointer<GetResourceDelegate>(fn);
        d(viewPtr, out var resource);
        return resource;
    }

    // IDXGISwapChain::GetBuffer via raw vtable (slot 9) — returns +1 reference.
    private static IntPtr SwapChainGetBuffer(IntPtr chainPtr, int index)
    {
        var vtbl = Marshal.ReadIntPtr(chainPtr);
        var fn = Marshal.ReadIntPtr(vtbl + 9 * IntPtr.Size);
        var d = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(fn);
        var guid = typeof(ID3D11Texture2D).GUID;
        d(chainPtr, (uint)index, ref guid, out var surface);
        return surface;
    }

    private long PresentDetour(IntPtr swapChainPtr, uint sync, uint flags)
    {
        if (!failed)
        {
            try
            {
                Draw(swapChainPtr);
            }
            catch (Exception ex)
            {
                failed = true;
                if (loggedErrors++ < 3)
                    log.Error($"[DxScreen] present draw failed, disabling: {ex.Message}");
            }
        }

        return presentHook!.Original(swapChainPtr, sync, flags);
    }

    // Borrowed per Present call; wrapped with an extra reference so Dispose
    // does not release the game's swap chain.
    private IDXGISwapChain? currentChain;

    // --- drawing ---------------------------------------------------------

    private void Draw(IntPtr swapChainPtr)
    {
        List<ScreenQuad> drawQuads;
        Matrix4x4 vp;
        Dictionary<string, (byte[] Rgba, int W, int H)> frames;
        lock (frameLock)
        {
            if (!hasViewProj || quads.Count == 0)
                return;
            vp = viewProj;
            drawQuads = quads;
            frames = new Dictionary<string, (byte[], int, int)>(pendingFrames.Count);
            foreach (var (id, rgba) in pendingFrames)
            {
                if (pendingSizes.TryGetValue(id, out var size))
                    frames[id] = (rgba, size.W, size.H);
            }
        }

        Marshal.AddRef(swapChainPtr); // wrapper's Dispose must not release the game's chain
        currentChain = new IDXGISwapChain(swapChainPtr);
        try
        {
            if (!resourcesReady)
            {
                if (!InitResources(currentChain))
                    return;
            }

            RefreshBackbuffer(currentChain);
            if (backbufferView == null)
                return;

            UploadFrames(frames);

            var dsvPtr = capturedDsv;
            if (dsvPtr == IntPtr.Zero)
                return;

            if (capturedDsvWrapper == null || capturedDsvWrapper.NativePointer != dsvPtr)
            {
                capturedDsvWrapper?.Dispose();
                Marshal.AddRef(dsvPtr); // wrapper owns this reference; game keeps its own
                capturedDsvWrapper = new ID3D11DepthStencilView(dsvPtr);
                reverseZ = null;
            }

            if (reverseZ == null)
                DetectReverseZ();

            DrawQuads(drawQuads, vp);
        }
        finally
        {
            currentChain?.Dispose();
            currentChain = null;
        }
    }

    private static Blob CompileShader(string hlsl, string profile)
    {
        var src = System.Text.Encoding.ASCII.GetBytes(hlsl + "\0");
        unsafe
        {
            fixed (byte* p = src)
            {
                var result = Compiler.Compile(p, src.Length, null, null, null, "main", profile,
                    ShaderFlags.None, EffectFlags.None, out var code, out var errors);
                if (result.Failure)
                {
                    var msg = errors != null ? Marshal.PtrToStringAnsi(errors.BufferPointer) : result.ToString();
                    throw new InvalidOperationException($"shader compile failed: {msg}");
                }

                return code;
            }
        }
    }

    private bool InitResources(IDXGISwapChain chain)
    {
        device = chain.GetDevice<IDXGIDevice>().QueryInterface<ID3D11Device>();
        context = device.ImmediateContext;

        using var vsBlob = CompileShader(VertexShaderHlsl, "vs_5_0");
        using var psBlob = CompileShader(PixelShaderHlsl, "ps_5_0");

        unsafe
        {
            vs = device.CreateVertexShader((void*)vsBlob.BufferPointer, vsBlob.BufferSize, null);
            ps = device.CreatePixelShader((void*)psBlob.BufferPointer, psBlob.BufferSize, null);
        }

        var bytecode = new byte[(int)vsBlob.BufferSize];
        Marshal.Copy(vsBlob.BufferPointer, bytecode, 0, (int)vsBlob.BufferSize);

        layout = device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 12),
        }, bytecode);

        device.CreateBuffer(new BufferDescription(4 * 16 * Marshal.SizeOf<Vertex>(),
            BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write), null, out vertexBuffer);

        indexBuffer = device.CreateBuffer(BuildIndices(),
            BindFlags.IndexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None, ResourceOptionFlags.None, 0, 0);

        device.CreateBuffer(new BufferDescription(Marshal.SizeOf<Constants>(),
            BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write), null, out constantBuffer);

        blendState = device.CreateBlendState(BlendDescription.AlphaBlend);

        depthState = device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        depthStateReverse = device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual));

        rasterState = device.CreateRasterizerState(RasterizerDescription.CullNone);

        samplerState = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0f, 0, ComparisonFunction.Never, 0f, 0f));

        resourcesReady = true;
        log.Information("[DxScreen] DX resources initialised");
        return true;
    }

    private static uint[] BuildIndices()
    {
        var idx = new uint[6 * 16];
        for (var q = 0; q < 16; q++)
        {
            var o = (uint)(q * 4);
            var i = q * 6;
            idx[i + 0] = o + 0; idx[i + 1] = o + 1; idx[i + 2] = o + 2;
            idx[i + 3] = o + 0; idx[i + 4] = o + 2; idx[i + 5] = o + 3;
        }
        return idx;
    }

    private void RefreshBackbuffer(IDXGISwapChain chain)
    {
        var bb = chain.Description.BufferDescription;
        if (backbufferW == bb.Width && backbufferH == bb.Height && backbufferView != null)
            return;

        backbufferView?.Dispose();
        backbufferView = null;
        try
        {
            var bbPtr = SwapChainGetBuffer(chain.NativePointer, 0);
            using var buffer = new ID3D11Texture2D(bbPtr); // GetBuffer returns with a reference
            backbufferView = device!.CreateRenderTargetView(buffer);
            backbufferW = bb.Width;
            backbufferH = bb.Height;
        }
        catch
        {
            backbufferView = null;
        }
    }

    private void UploadFrames(Dictionary<string, (byte[] Rgba, int W, int H)> frames)
    {
        foreach (var (id, (rgba, w, h)) in frames)
        {
            if (!textures.TryGetValue(id, out var qt) || qt.W != w || qt.H != h)
            {
                qt?.Dispose();
                qt = new QuadTexture();
                textures[id] = qt;

                qt.Texture = device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = w,
                    Height = h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                qt.Srv = device.CreateShaderResourceView(qt.Texture);
                qt.W = w;
                qt.H = h;
            }

            context!.Map(qt.Texture!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None, out var mapped);
            try
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(rgba, y * w * 4, mapped.DataPointer + y * mapped.RowPitch, w * 4);
            }
            finally
            {
                context.Unmap(qt.Texture!, 0);
            }
        }
    }

    private void DetectReverseZ()
    {
        // Read one pixel near the top of the captured depth view: sky/far.
        // Standard depth has far = 1, reverse-Z has far = 0.
        try
        {
            var resPtr = ViewGetResource(capturedDsv);
            if (resPtr == IntPtr.Zero)
            {
                reverseZ = false;
                return;
            }
            using var resource = new ID3D11Resource(resPtr);
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;
            var isD24 = (int)desc.Format == DxgiFormatD24UNormS8UInt;

            var stagingDesc = new Texture2DDescription
            {
                Width = 4,
                Height = 4,
                MipLevels = 1,
                ArraySize = 1,
                Format = isD24 ? (Format)DxgiFormatD24UNormS8UInt : (Format)DxgiFormatD32Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            };

            // Staging copy of a depth texture needs a typeless format for
            // D24; if creation fails, fall back to assuming standard depth.
            ID3D11Texture2D? staging;
            try
            {
                staging = device!.CreateTexture2D(stagingDesc);
            }
            catch
            {
                reverseZ = false;
                return;
            }

            using (staging)
            {
                context!.CopySubresourceRegion(texture, 0, 0, 0, 0, staging, 0);
                context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
                try
                {
                    var raw = Marshal.ReadInt32(mapped.DataPointer);
                    float depth;
                    if (isD24)
                        depth = (raw & 0x00FFFFFF) / (float)0x00FFFFFF;
                    else
                    {
                        unsafe { depth = *(float*)&raw; }
                    }

                    reverseZ = depth < 0.5f;
                    log.Information($"[DxScreen] depth probe: far pixel {depth:F3} → {(reverseZ == true ? "reverse-Z" : "standard")}");
                }
                finally
                {
                    context.Unmap(staging, 0);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"[DxScreen] depth probe failed: {ex.Message}");
            reverseZ = false;
        }
    }

    private void DrawQuads(List<ScreenQuad> drawQuads, Matrix4x4 vp)
    {
        var ctx = context!;

        // Upload vertices for this frame (4 verts per quad, world space).
        var verts = new Vertex[drawQuads.Count * 4];
        for (var q = 0; q < drawQuads.Count; q++)
        {
            var quad = drawQuads[q];

            // Camera-facing billboard axes from the view-proj rows.
            var right = new Vector3(vp.M11, vp.M21, vp.M31);
            var up = new Vector3(vp.M12, vp.M22, vp.M32);
            if (right.LengthSquared() > 1e-8f) right = Vector3.Normalize(right);
            if (up.LengthSquared() > 1e-8f) up = Vector3.Normalize(up);

            var c = quad.Center;
            var r = right * quad.HalfWidth;
            var u = up * quad.HalfHeight;

            verts[q * 4 + 0] = new Vertex { Position = c - r + u, Uv = new Vector2(0, 0) };
            verts[q * 4 + 1] = new Vertex { Position = c + r + u, Uv = new Vector2(1, 0) };
            verts[q * 4 + 2] = new Vertex { Position = c + r - u, Uv = new Vector2(1, 1) };
            verts[q * 4 + 3] = new Vertex { Position = c - r - u, Uv = new Vector2(0, 1) };
        }

        var vmapHandle = GCHandle.Alloc(verts, GCHandleType.Pinned);
        try
        {
            ctx.Map(vertexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var vmap);
            unsafe
            {
                Buffer.MemoryCopy((void*)vmapHandle.AddrOfPinnedObject(), (void*)vmap.DataPointer,
                    (long)verts.Length * Marshal.SizeOf<Vertex>(), (long)verts.Length * Marshal.SizeOf<Vertex>());
            }
            ctx.Unmap(vertexBuffer!, 0);
        }
        finally
        {
            vmapHandle.Free();
        }

        var cb = new Constants { ViewProj = vp };
        ctx.Map(constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var cmap);
        Marshal.StructureToPtr(cb, cmap.DataPointer, false);
        ctx.Unmap(constantBuffer!, 0);

        // Bind pipeline.
        ctx.OMSetRenderTargets(new[] { backbufferView! }, capturedDsvWrapper);
        ctx.OMSetDepthStencilState(reverseZ == true ? depthStateReverse! : depthState!, 0);
        unsafe { ctx.OMSetBlendState(blendState!, (float*)null, 0xFFFFFFFFu); }
        ctx.RSSetState(rasterState!);

        var viewport = new VorticeMath.Viewport(0, 0, backbufferW, backbufferH);
        ctx.RSSetViewports(new[] { viewport });

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(layout!);

        ctx.IASetVertexBuffers(0, new[] { vertexBuffer! }, new[] { Marshal.SizeOf<Vertex>() }, new[] { 0 });
        ctx.IASetIndexBuffer(indexBuffer!, Format.R32_UInt, 0);

        ctx.VSSetShader(vs!);
        ctx.VSSetConstantBuffers(0, new[] { constantBuffer! });
        ctx.PSSetShader(ps!);
        ctx.PSSetSamplers(0, new[] { samplerState! });

        for (var q = 0; q < drawQuads.Count; q++)
        {
            var id = drawQuads[q].Id;
            if (!textures.TryGetValue(id, out var qt) || qt.Srv == null)
                continue;
            ctx.PSSetShaderResources(0, new[] { qt.Srv });
            ctx.DrawIndexed(6, q * 6, 0);
        }

        // Unbind so the game's next pass starts clean.
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        ctx.OMSetRenderTargets(new ID3D11RenderTargetView[] { null! }, null);
    }

    private const string VertexShaderHlsl = @"
cbuffer CB : register(b0) { float4x4 vp; };
struct VS_IN { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct VS_OUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VS_OUT main(VS_IN i)
{
    VS_OUT o;
    o.pos = mul(vp, float4(i.pos, 1.0));
    o.uv = i.uv;
    return o;
}";

    private const string PixelShaderHlsl = @"
Texture2D tex : register(t0);
SamplerState samp : register(s0);
struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(PS_IN i) : SV_TARGET
{
    return tex.Sample(samp, i.uv);
}";

    public void Dispose()
    {
        Disable();
        presentHook?.Dispose();
        omHook?.Dispose();
        presentHook = null;
        omHook = null;

        foreach (var qt in textures.Values)
            qt.Dispose();
        textures.Clear();
        capturedDsvWrapper?.Dispose();
        backbufferView?.Dispose();
        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();
        constantBuffer?.Dispose();
        vs?.Dispose();
        ps?.Dispose();
        layout?.Dispose();
        blendState?.Dispose();
        depthState?.Dispose();
        depthStateReverse?.Dispose();
        rasterState?.Dispose();
        samplerState?.Dispose();
    }
}
