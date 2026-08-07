using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FfxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using FfxivTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using FfxivRenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using VorticeMath = Vortice.Mathematics;

namespace RetroXIV.Rendering;

// Depth-integrated world screen: draws the video quads inside the game's
// DX11 frame with a read-only depth test against the scene depth buffer, so
// characters and geometry occlude the screens.
//
// Patch-resilience by construction:
//  - Present (vtable 8) is found through a throwaway dummy swap chain. The
//    real immediate context supplies its bound depth view at Present; no
//    generic device-context hook is used for resource discovery.
//  - The view-projection matrix is reconstructed every frame on the game
//    thread from IGameGui.WorldToScreen projections (DLT least squares) —
//    no game signatures, no struct offsets.
//  - Reverse-Z vs standard depth is auto-detected by reading one sky pixel.
public sealed class DxWorldRenderer : IDisposable
{
    private const int PresentVtableIndex = 8;

    // DXGI_FORMAT values used numerically to dodge binding-name roulette.
    private const int DxgiFormatD24UNormS8UInt = 45;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long PresentDelegate(IntPtr swapChain, uint syncInterval, uint flags);

    // ID3D11View::GetResource — vtable slot 4 (after IUnknown + GetDevice).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]

    // IDXGISwapChain::GetBuffer — vtable slot 9.
    private delegate long GetBufferDelegate(IntPtr self, uint buffer, ref Guid riid, out IntPtr surface);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    public struct ScreenQuad
    {
        public string Id;
        public Vector3 Center;
        public Vector3 Right;
        public Vector3 Up;
        public float HalfWidth;
        public float HalfHeight;
    }

    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;

    private Hook<PresentDelegate>? presentHook;

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
    private ID3D11PixelShader? visibilityProbePs;
    private ID3D11InputLayout? layout;
    private ID3D11Buffer? vertexBuffer;
    private ID3D11Buffer? indexBuffer;
    private ID3D11Buffer? constantBuffer;
    private ID3D11BlendState? blendState;
    private ID3D11DepthStencilState? depthState;
    private ID3D11DepthStencilState? depthStateReverse;
    private ID3D11DepthStencilState? depthStateDisabled;
    private ID3D11RasterizerState? rasterState;
    private ID3D11SamplerState? samplerState;
    private ID3D11RenderTargetView? backbufferView;
    private int backbufferW, backbufferH;

    private readonly Dictionary<string, QuadTexture> textures = new();
    private ID3D11DepthStencilView? capturedDsvWrapper;
    private ID3D11ShaderResourceView? capturedDepthSrv;
    // Retained only for the FFXIV-tracked depth texture. This lets depth
    // readback use a normal typed wrapper instead of a raw view vtable call.
    private ID3D11Texture2D? capturedDepthTexture;
    private IntPtr capturedFfxivDepthTexture;
    private int capturedDepthContentW;
    private int capturedDepthContentH;
    private bool scannedSceneDepthCandidates;
    private int selectedSceneDepthOffset = 0x88;
    private bool? reverseZ;
    private bool row3Calibrated;
    private List<(Vector3 P, Vector2 Pixel)> calibPoints = new();
    private bool resourcesReady;
    private bool failed;
    private int loggedErrors;
    private DateTime lastIdleLog = DateTime.MinValue;
    private DateTime lastCalibrationLog = DateTime.MinValue;
    private DateTime lastQuadDiagnosticLog = DateTime.MinValue;
    private DateTime nextFfxivDepthProbe = DateTime.MinValue;
    private DateTime nextRow3Calibration = DateTime.MinValue;
    private string? loggedDepthTextureDescription;
    private bool loggedFirstDraw;
    private readonly HashSet<string> loggedFrameDiagnostics = new();
    private readonly HashSet<string> loggedGpuFrameDiagnostics = new();
    // Set temporarily above zero when validating Present/backbuffer injection.
    private int visibilityProbeFrames;
    private bool loggedVisibilityProbe;
    // Solid, depth-disabled draw of the actual world vertices. This separates
    // world transform/rasterization from texture sampling and depth rejection.
    private int worldGeometryProbeFrames;
    private bool loggedWorldGeometryProbe;
    private bool loggedTextureWithoutDepthProbe;
    private long preparedFrameSerial;
    private long drawnFrameSerial;

    private void LogIdle(string reason)
    {
        if ((DateTime.UtcNow - lastIdleLog).TotalSeconds < 3)
            return;
        lastIdleLog = DateTime.UtcNow;
        log.Information($"[DxScreen] idle: {reason}");
    }

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
        public Vector4 DepthInfo;
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
                presentHook.Enable();
            return;
        }

        try
        {
            var presentAddr = FindPresentAddress();
            presentHook = interop.HookFromAddress<PresentDelegate>(presentAddr, PresentDetour);
            presentHook.Enable();
            log.Information("[DxScreen] Present world-screen hook installed (native UI may overlap the video)");
        }
        catch (Exception ex)
        {
            presentHook?.Dispose();
            presentHook = null;
            failed = true;
            log.Error($"[DxScreen] hook install failed: {ex.Message}");
        }
    }

    public void Disable()
    {
        presentHook?.Disable();
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

    private static IntPtr FindPresentAddress()
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
                return present;
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
        Dictionary<string, (byte[] Rgba, int W, int H)> frames,
        List<(Vector3 P, Vector2 Pixel)> calib)
    {
        lock (frameLock)
        {
            hasViewProj = viewProj.HasValue;
            if (viewProj.HasValue)
            {
                // The active FFXIV scene camera supplies the complete view-
                // projection matrix, including clip Z. Do not replace its
                // depth column with the old 2D-estimator calibration.
                this.viewProj = viewProj.Value;
                row3Calibrated = true;
            }
            this.quads = quads;
            calibPoints = calib;
            pendingFrames.Clear();
            pendingSizes.Clear();
            foreach (var (id, (rgba, w, h)) in frames)
            {
                pendingFrames[id] = rgba;
                pendingSizes[id] = (w, h);
            }
        }
    }

    // --- depth acquisition (render thread) ------------------------------

    // OMGetRenderTargets is invoked on FFXIV's actual immediate context and
    // DirectX returns a ref-counted ID3D11DepthStencilView. This is the
    // supported way to obtain the current binding; unlike a generic detour,
    // it cannot reinterpret unrelated call arguments as COM pointers.
    private bool TryAcquireBoundDepth()
    {
        // Prefer the retained world-scene target. Present normally has no DSV
        // bound, and any late UI/swap-chain DSV is not the geometry depth.
        if (TryAcquireFfxivDepthTexture())
            return true;

        ID3D11DepthStencilView? boundDepth = null;
        try
        {
            context!.OMGetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), out boundDepth);
            if (boundDepth == null || boundDepth.NativePointer == IntPtr.Zero)
            {
                boundDepth?.Dispose();
                return TryAcquireFfxivDepthTexture();
            }

            return AdoptDepthView(boundDepth, "the immediate context at Present");
        }
        catch (Exception ex)
        {
            boundDepth?.Dispose();
            LogIdle($"OMGetRenderTargets failed: {ex.Message}");
            return TryAcquireFfxivDepthTexture();
        }
    }

    // FFXIV tracks its depth texture in the patch-maintained client structs.
    // This fallback is used only when the output merger has already unbound
    // the depth view by Present; it never discovers resources from a hook
    // argument.
    private unsafe bool TryAcquireFfxivDepthTexture()
    {
        var hasCachedDepth = capturedDepthTexture != null
            && capturedDepthTexture.NativePointer != IntPtr.Zero;
        if (DateTime.UtcNow < nextFfxivDepthProbe && hasCachedDepth)
            return true;

        if (DateTime.UtcNow < nextFfxivDepthProbe)
            return false;
        nextFfxivDepthProbe = DateTime.UtcNow.AddSeconds(3);

        ID3D11Texture2D? texture = null;
        try
        {
            var gameDevice = FfxivDevice.Instance();
            if (gameDevice == null)
            {
                LogIdle("FFXIV Device singleton is unavailable");
                return false;
            }

            // The swap-chain/current depth target has already been cleared to
            // reverse-Z far by Present. RenderTargetManager retains the main
            // scene depth used for deferred rendering, including characters
            // and world geometry, which is the buffer this late draw needs.
            FfxivTexture* gameDepth = null;
            var depthSource = "FFXIV's tracked scene depth texture";
            var renderTargets = FfxivRenderTargetManager.Instance();
            if (renderTargets != null)
                gameDepth = SelectSceneDepthTexture(renderTargets, out depthSource);
            if (gameDepth == null && gameDevice->ImmediateContext != null)
            {
                gameDepth = gameDevice->ImmediateContext->CurrentDepthStencilBuffer;
                depthSource = "FFXIV's current depth texture fallback";
            }
            if (gameDepth == null && gameDevice->SwapChain != null)
            {
                gameDepth = gameDevice->SwapChain->DepthStencil;
                depthSource = "FFXIV's swap-chain depth texture fallback";
            }
            if (gameDepth == null || gameDepth->D3D11Texture2D == null)
            {
                LogIdle($"FFXIV depth texture unavailable (immediate context: {gameDevice->ImmediateContext != null}, "
                    + $"swap chain: {gameDevice->SwapChain != null}, current depth: "
                    + $"{(gameDevice->ImmediateContext != null && gameDevice->ImmediateContext->CurrentDepthStencilBuffer != null)}, "
                    + $"swap-chain depth: {(gameDevice->SwapChain != null && gameDevice->SwapChain->DepthStencil != null)})");
                return false;
            }

            var texturePtr = (IntPtr)gameDepth->D3D11Texture2D;
            if (capturedDepthTexture != null && capturedFfxivDepthTexture == texturePtr)
                return true;

            Marshal.AddRef(texturePtr); // the adopted wrapper owns this reference
            texture = new ID3D11Texture2D(texturePtr);
            var textureDesc = texture.Description;
            var description = $"{textureDesc.Width}x{textureDesc.Height}, format {(int)textureDesc.Format}, "
                + $"bind {textureDesc.BindFlags}, samples {textureDesc.SampleDescription.Count}, array {textureDesc.ArraySize}";
            if (loggedDepthTextureDescription != description)
            {
                loggedDepthTextureDescription = description;
                log.Information($"[DxScreen] FFXIV depth texture: {description}");
            }

            var contentW = (int)gameDepth->ActualWidth;
            var contentH = (int)gameDepth->ActualHeight;
            if ((textureDesc.BindFlags & BindFlags.DepthStencil) == 0
                && (textureDesc.BindFlags & BindFlags.ShaderResource) != 0)
            {
                // FFXIV's retained post-geometry depth copy is SRV-only. A
                // DSV cannot legally be created from it, so compare it in the
                // pixel shader instead of using fixed-function depth.
                var srvDesc = new ShaderResourceViewDescription(texture,
                    ShaderResourceViewDimension.Texture2D, (Format)46, 0, 1, 0, 1);
                var depthSrv = device!.CreateShaderResourceView(texture, srvDesc);
                AdoptDepthShaderResource(depthSrv, texture,
                    $"{depthSource} ({contentW}x{contentH})", contentW, contentH);
                texture = null;
                capturedFfxivDepthTexture = texturePtr;
                return true;
            }

            var dsvFormat = GetDepthStencilViewFormat(textureDesc.Format);
            var dsvDimension = textureDesc.SampleDescription.Count > 1
                ? (textureDesc.ArraySize > 1
                    ? DepthStencilViewDimension.Texture2DMultisampledArray
                    : DepthStencilViewDimension.Texture2DMultisampled)
                : (textureDesc.ArraySize > 1
                    ? DepthStencilViewDimension.Texture2DArray
                    : DepthStencilViewDimension.Texture2D);
            var dsvDesc = new DepthStencilViewDescription(
                dsvDimension, dsvFormat, 0, 0, textureDesc.ArraySize, DepthStencilViewFlags.None);
            var depthView = device!.CreateDepthStencilView(texture, dsvDesc);
            var adopted = AdoptDepthView(depthView,
                $"{depthSource} ({contentW}x{contentH})", texture, contentW, contentH);
            texture = null; // AdoptDepthView consumed it on both outcomes.
            if (adopted)
                capturedFfxivDepthTexture = texturePtr;
            return adopted;
        }
        catch (Exception ex)
        {
            texture?.Dispose();
            LogIdle($"FFXIV depth-texture fallback failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void AdoptDepthShaderResource(ID3D11ShaderResourceView depthSrv,
        ID3D11Texture2D texture, string source, int contentW, int contentH)
    {
        capturedDsvWrapper?.Dispose();
        capturedDepthSrv?.Dispose();
        capturedDepthTexture?.Dispose();
        capturedDsvWrapper = null;
        capturedDepthSrv = depthSrv;
        capturedDepthTexture = texture;
        capturedDepthContentW = contentW;
        capturedDepthContentH = contentH;
        capturedFfxivDepthTexture = IntPtr.Zero;
        reverseZ = null;
        row3Calibrated = false;
        log.Information($"[DxScreen] acquired shader-readable depth from {source}");
    }

    private unsafe FfxivTexture* SelectSceneDepthTexture(
        FfxivRenderTargetManager* renderTargets, out string source)
    {
        source = $"FFXIV's retained depth sibling 0x{selectedSceneDepthOffset:X}";
        if (scannedSceneDepthCandidates)
            return *(FfxivTexture**)((byte*)renderTargets + selectedSceneDepthOffset);

        scannedSceneDepthCandidates = true;
        var candidates = new (int Offset, string Name)[]
        {
            (0x70, "DepthStencil"),
            (0x78, "depth sibling 0x78"),
            (0x80, "depth sibling 0x80"),
            (0x88, "depth sibling 0x88"),
            (0x90, "depth sibling 0x90"),
            (0xE8, "semitransparent depth sibling 0xE8"),
        };

        var seen = new HashSet<IntPtr>();
        var bestDepth = -1f;
        var foundPreferredSceneCopy = false;
        foreach (var (offset, name) in candidates)
        {
            var candidate = *(FfxivTexture**)((byte*)renderTargets + offset);
            if (candidate == null || candidate->D3D11Texture2D == null)
                continue;

            var native = (IntPtr)candidate->D3D11Texture2D;
            if (!seen.Add(native))
                continue;

            try
            {
                Marshal.AddRef(native);
                using var texture = new ID3D11Texture2D(native);
                var desc = texture.Description;
                if (!IsD24DepthFormat(desc.Format))
                {
                    log.Information($"[DxScreen] depth candidate {name}: {desc.Width}x{desc.Height}, "
                        + $"format {(int)desc.Format}, not D24");
                    continue;
                }

                var contentW = Math.Clamp((int)candidate->ActualWidth, 1, desc.Width);
                var contentH = Math.Clamp((int)candidate->ActualHeight, 1, desc.Height);
                var maxDepth = 0f;
                foreach (var (fx, fy) in new[]
                {
                    (0.20f, 0.25f), (0.50f, 0.25f), (0.80f, 0.25f),
                    (0.20f, 0.50f), (0.50f, 0.50f), (0.80f, 0.50f),
                    (0.20f, 0.75f), (0.50f, 0.75f), (0.80f, 0.75f),
                })
                {
                    var value = ReadDepthAt(texture, true,
                        Math.Min(contentW - 1, (int)(contentW * fx)),
                        Math.Min(contentH - 1, (int)(contentH * fy)));
                    if (value.HasValue)
                        maxDepth = Math.Max(maxDepth, value.Value);
                }

                log.Information($"[DxScreen] depth candidate {name}: {contentW}x{contentH} "
                    + $"in {desc.Width}x{desc.Height}, max sample {maxDepth:G8}");
                var shaderReadableCopy = offset == 0x88
                    && (desc.BindFlags & BindFlags.ShaderResource) != 0
                    && (desc.BindFlags & BindFlags.DepthStencil) == 0;
                if (shaderReadableCopy)
                {
                    // This sibling remains synchronized with world geometry
                    // through Present. DepthStencil/0x70 may have a larger
                    // numerical sample but becomes stale during camera motion.
                    foundPreferredSceneCopy = true;
                    bestDepth = maxDepth;
                    selectedSceneDepthOffset = offset;
                    source = $"FFXIV's retained {name}";
                }
                else if (!foundPreferredSceneCopy && maxDepth > bestDepth)
                {
                    bestDepth = maxDepth;
                    selectedSceneDepthOffset = offset;
                    source = $"FFXIV's retained {name}";
                }
            }
            catch (Exception ex)
            {
                log.Information($"[DxScreen] depth candidate {name} skipped: {ex.Message}");
            }
        }

        log.Information($"[DxScreen] selected scene depth offset 0x{selectedSceneDepthOffset:X} "
            + $"(max sample {bestDepth:G8})");
        return *(FfxivTexture**)((byte*)renderTargets + selectedSceneDepthOffset);
    }

    private bool AdoptDepthView(ID3D11DepthStencilView candidate, string source,
        ID3D11Texture2D? backingTexture = null, int contentW = 0, int contentH = 0)
    {
        if (candidate.NativePointer == IntPtr.Zero)
        {
            candidate.Dispose();
            backingTexture?.Dispose();
            return false;
        }

        if (capturedDsvWrapper != null && capturedDsvWrapper.NativePointer == candidate.NativePointer)
        {
            candidate.Dispose(); // the caller received an extra reference
            backingTexture?.Dispose();
            return true;
        }

        capturedDsvWrapper?.Dispose();
        capturedDepthSrv?.Dispose();
        capturedDepthTexture?.Dispose();
        capturedDsvWrapper = candidate;
        capturedDepthSrv = null;
        capturedDepthTexture = backingTexture;
        capturedDepthContentW = contentW;
        capturedDepthContentH = contentH;
        capturedFfxivDepthTexture = IntPtr.Zero;
        reverseZ = null;
        row3Calibrated = false;
        nextRow3Calibration = DateTime.MinValue;
        log.Information($"[DxScreen] acquired depth view from {source}");
        return true;
    }

    private static Format GetDepthStencilViewFormat(Format textureFormat) => (int)textureFormat switch
    {
        19 => (Format)20, // R32G8X24_TYPELESS → D32_FLOAT_S8X24_UINT
        39 => (Format)40, // R32_TYPELESS → D32_FLOAT
        44 => (Format)45, // R24G8_TYPELESS → D24_UNORM_S8_UINT
        53 => (Format)55, // R16_TYPELESS → D16_UNORM
        _ => textureFormat,
    };

    private static bool IsD24DepthFormat(Format format)
        => (int)format == 44 || (int)format == DxgiFormatD24UNormS8UInt;

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
                PrepareFrame(swapChainPtr);
                DrawPreparedFrameAtPresent();
            }
            catch (Exception ex)
            {
                failed = true;
                if (loggedErrors++ < 3)
                    log.Error($"[DxScreen] present preparation failed, disabling: {ex}");
            }
        }

        return presentHook!.Original(swapChainPtr, sync, flags);
    }

    // --- drawing ---------------------------------------------------------

    // Present is deliberately the draw point. This keeps the stable world and
    // character depth path, but the native HUD may be covered by the video.
    private void PrepareFrame(IntPtr swapChainPtr)
    {
        Dictionary<string, (byte[] Rgba, int W, int H)> frames;
        bool needsDraw;
        lock (frameLock)
        {
            needsDraw = quads.Count != 0;
            if (needsDraw && !hasViewProj)
            {
                LogIdle($"camera estimation failing: {CameraEstimator.LastDiagnostic}");
                return;
            }
            frames = new Dictionary<string, (byte[], int, int)>(pendingFrames.Count);
            foreach (var (id, rgba) in pendingFrames)
            {
                if (pendingSizes.TryGetValue(id, out var size))
                    frames[id] = (rgba, size.W, size.H);
            }
        }

        Marshal.AddRef(swapChainPtr); // wrapper's Dispose must not release the game's chain
        using var chain = new IDXGISwapChain(swapChainPtr);
        if (!EnsureDeviceContext(chain))
            return;

        // Keep depth discovery observable across plugin reloads, even
        // before the user has restarted a live stream.
        if (!needsDraw)
        {
            if (!TryAcquireBoundDepth())
                LogIdle("no usable FFXIV depth source available at Present");
            return;
        }

        if (!resourcesReady && !InitResources(chain))
            return;

        RefreshBackbuffer(chain);
        if (backbufferView == null)
        {
            LogIdle("no backbuffer");
            return;
        }

        UploadFrames(frames);

        if (!TryAcquireBoundDepth())
        {
            LogIdle("no retained scene depth available at Present");
            return;
        }

        if (!row3Calibrated)
        {
            LogIdle("full scene-camera projection pending");
            return;
        }

        if (reverseZ == null && !DetectReverseZ())
        {
            LogIdle("depth source has no safe typed texture for readback");
            return;
        }

        preparedFrameSerial++;
    }

    private void DrawPreparedFrameAtPresent()
    {
        if (preparedFrameSerial == 0 || drawnFrameSerial == preparedFrameSerial
            || !resourcesReady || context == null || backbufferView == null)
            return;

        List<ScreenQuad> drawQuads;
        Matrix4x4 vp;
        lock (frameLock)
        {
            if (quads.Count == 0 || !hasViewProj)
                return;
            drawQuads = quads;
            vp = viewProj;
        }

        // Consume once per Present call.
        drawnFrameSerial = preparedFrameSerial;

        ID3D11DepthStencilView? previousDepth = null;
        var previousTargets = new ID3D11RenderTargetView[1];
        try
        {
            context.OMGetRenderTargets(1, previousTargets, out previousDepth);

            if (!loggedFirstDraw)
            {
                loggedFirstDraw = true;
                log.Information($"[DxScreen] first Present draw ({drawQuads.Count} quads, reverseZ={reverseZ})");
            }

            DrawQuads(drawQuads, vp);
        }
        finally
        {
            // DrawQuads unbinds its SRVs; restore the target that was active
            // on entry even though Present normally has none bound.
            context.OMSetRenderTargets(previousTargets, previousDepth);
            previousTargets[0]?.Dispose();
            previousDepth?.Dispose();
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
        if (!EnsureDeviceContext(chain))
            return false;
        var dxDevice = device!;

        using var vsBlob = CompileShader(VertexShaderHlsl, "vs_5_0");
        using var psBlob = CompileShader(PixelShaderHlsl, "ps_5_0");
        using var visibilityProbePsBlob = CompileShader(VisibilityProbePixelShaderHlsl, "ps_5_0");

        unsafe
        {
            vs = dxDevice.CreateVertexShader((void*)vsBlob.BufferPointer, vsBlob.BufferSize, null);
            ps = dxDevice.CreatePixelShader((void*)psBlob.BufferPointer, psBlob.BufferSize, null);
            visibilityProbePs = dxDevice.CreatePixelShader(
                (void*)visibilityProbePsBlob.BufferPointer, visibilityProbePsBlob.BufferSize, null);
        }

        var bytecode = new byte[(int)vsBlob.BufferSize];
        Marshal.Copy(vsBlob.BufferPointer, bytecode, 0, (int)vsBlob.BufferSize);

        layout = dxDevice.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            // Constructor order is (byte offset, input slot). UV follows the
            // 12-byte position in the same interleaved vertex buffer.
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        }, bytecode);

        dxDevice.CreateBuffer(new BufferDescription(4 * 16 * Marshal.SizeOf<Vertex>(),
            BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write), null, out vertexBuffer);

        indexBuffer = dxDevice.CreateBuffer(BuildIndices(),
            BindFlags.IndexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None, ResourceOptionFlags.None, 0, 0);

        dxDevice.CreateBuffer(new BufferDescription(Marshal.SizeOf<Constants>(),
            BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write), null, out constantBuffer);

        blendState = dxDevice.CreateBlendState(BlendDescription.AlphaBlend);

        depthState = dxDevice.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        depthStateReverse = dxDevice.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual));
        depthStateDisabled = dxDevice.CreateDepthStencilState(
            new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));

        // The quad vertices are wound clockwise when viewed from their saved
        // surface normal. Keep that visible side and reject the reverse
        // winding so a screen has no picture when viewed from behind.
        var rasterDescription = RasterizerDescription.CullNone;
        rasterDescription.CullMode = CullMode.Back;
        rasterState = dxDevice.CreateRasterizerState(rasterDescription);

        samplerState = dxDevice.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0f, 0, ComparisonFunction.Never, 0f, 0f));

        resourcesReady = true;
        log.Information("[DxScreen] DX resources initialised");
        return true;
    }

    private bool EnsureDeviceContext(IDXGISwapChain chain)
    {
        if (device != null && context != null)
            return true;

        device = chain.GetDevice<IDXGIDevice>().QueryInterface<ID3D11Device>();
        context = device.ImmediateContext;
        return context != null;
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
            var logFrame = loggedFrameDiagnostics.Add(id);
            var cpuHash = logFrame ? LogFrameDiagnostic(id, rgba, w, h) : 0UL;

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

            context!.Map(qt.Texture!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
            if (mapped.DataPointer == IntPtr.Zero)
                throw new InvalidOperationException("texture map returned null pointer");
            try
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(rgba, y * w * 4, mapped.DataPointer + y * mapped.RowPitch, w * 4);
            }
            finally
            {
                context.Unmap(qt.Texture!, 0);
            }

            if (logFrame && loggedGpuFrameDiagnostics.Add(id))
                LogGpuFrameDiagnostic(id, qt.Texture!, w, h, cpuHash);
        }
    }

    private ulong LogFrameDiagnostic(string id, byte[] rgba, int w, int h)
    {
        byte minAlpha = byte.MaxValue;
        byte maxAlpha = byte.MinValue;
        var nonBlack = 0;
        ulong hash = 1469598103934665603UL;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0)
                nonBlack++;
            minAlpha = Math.Min(minAlpha, rgba[i + 3]);
            maxAlpha = Math.Max(maxAlpha, rgba[i + 3]);
            hash ^= rgba[i];
            hash *= 1099511628211UL;
            hash ^= rgba[i + 1];
            hash *= 1099511628211UL;
            hash ^= rgba[i + 2];
            hash *= 1099511628211UL;
            hash ^= rgba[i + 3];
            hash *= 1099511628211UL;
        }

        log.Information($"[DxScreen] frame diagnostic: id={id}, size={w}x{h}, bytes={rgba.Length}, "
            + $"alpha={minAlpha}-{maxAlpha}, nonBlack={nonBlack}/{w * h}, hash={hash:X16}");
        return hash;
    }

    private void LogGpuFrameDiagnostic(string id, ID3D11Texture2D texture, int w, int h, ulong cpuHash)
    {
        try
        {
            using var staging = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = w,
                Height = h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            context!.CopyResource(staging, texture);
            context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
            if (mapped.DataPointer == IntPtr.Zero)
            {
                log.Information($"[DxScreen] GPU frame diagnostic: id={id}, map returned null");
                return;
            }

            try
            {
                var row = new byte[w * 4];
                ulong gpuHash = 1469598103934665603UL;
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(mapped.DataPointer + y * mapped.RowPitch, row, 0, row.Length);
                    for (var i = 0; i < row.Length; i++)
                    {
                        gpuHash ^= row[i];
                        gpuHash *= 1099511628211UL;
                    }
                }
                log.Information($"[DxScreen] GPU frame diagnostic: id={id}, hash={gpuHash:X16}, "
                    + $"cpuHash={cpuHash:X16}, matches={gpuHash == cpuHash}, rowPitch={mapped.RowPitch}");
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        catch (Exception ex)
        {
            log.Information($"[DxScreen] GPU frame diagnostic failed: {ex.Message}");
        }
    }

    private bool DetectReverseZ()
    {
        // Read one pixel near the top of the captured depth view: sky/far.
        // Standard depth has far = 1, reverse-Z has far = 0.
        var texture = capturedDepthTexture;
        if (texture == null || texture.NativePointer == IntPtr.Zero)
            return false;

        try
        {
            var desc = texture.Description;
            var isD24 = IsD24DepthFormat(desc.Format);

            var stagingDesc = new Texture2DDescription
            {
                Width = 4,
                Height = 4,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
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
                return true;
            }

            using (staging)
            {
                var sourceBox = new VorticeMath.Box(0, 0, 0, 4, 4, 1);
                context!.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, sourceBox);
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

            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[DxScreen] depth probe failed: {ex.Message}");
            reverseZ = false;
            return true;
        }
    }

    // Clip Z is unobservable from 2D correspondences, so fit its coefficient
    // vector to the live depth buffer: for visible surface points p with
    // stored depth d, clip.z/clip.w = d ⇒ z·p = d·(w·p). Four
    // ScreenToWorld points give a 4x4 system.
    private bool TryCalibrateRow3()
    {
        List<(Vector3 P, Vector2 Pixel)> calib;
        Vector4 r4;
        lock (frameLock)
        {
            if (calibPoints.Count < 4)
                return false;
            calib = calibPoints;
            // System.Numerics transforms row vectors, so clip W is column 4.
            r4 = new Vector4(viewProj.M14, viewProj.M24, viewProj.M34, viewProj.M44);
        }

        var depthTex = capturedDepthTexture;
        if (depthTex == null || depthTex.NativePointer == IntPtr.Zero)
            return false;

        var isD24 = IsD24DepthFormat(depthTex.Description.Format);

        // Perspective depth has the form d = A + B/clipW, so clipZ is
        // A*clipW + B. Fit those two scalars instead of an unconstrained 4x4
        // row: it is both more stable and preserves the camera projection's
        // exact W plane as the camera moves.
        var valid = new List<(double InvW, double Depth)>();
        var minDepth = double.PositiveInfinity;
        var maxDepth = double.NegativeInfinity;
        for (var i = 0; i < 4; i++)
        {
            var (p, pixel) = calib[i];
            var depthW = capturedDepthContentW > 0 ? capturedDepthContentW : depthTex.Description.Width;
            var depthH = capturedDepthContentH > 0 ? capturedDepthContentH : depthTex.Description.Height;
            var depthPixelX = (int)(pixel.X * depthW / Math.Max(1, backbufferW));
            var depthPixelY = (int)(pixel.Y * depthH / Math.Max(1, backbufferH));
            var d = ReadDepthAt(depthTex, isD24, depthPixelX, depthPixelY);
            if (d == null)
                continue;

            var w = r4.X * p.X + r4.Y * p.Y + r4.Z * p.Z + r4.W;
            var isClear = reverseZ == true ? d.Value <= 1e-7f : d.Value >= 1f - 1e-7f;
            if (!isClear && Math.Abs(w) > 1e-6)
            {
                valid.Add((1.0 / w, d.Value));
                minDepth = Math.Min(minDepth, d.Value);
                maxDepth = Math.Max(maxDepth, d.Value);
            }
        }

        if (valid.Count < 2)
        {
            if ((DateTime.UtcNow - lastCalibrationLog).TotalSeconds >= 3)
            {
                lastCalibrationLog = DateTime.UtcNow;
                log.Information($"[DxScreen] depth row pending: {valid.Count}/4 scene samples were non-clear");
            }
            return false;
        }

        var meanX = 0.0;
        var meanY = 0.0;
        foreach (var sample in valid)
        {
            meanX += sample.InvW;
            meanY += sample.Depth;
        }
        meanX /= valid.Count;
        meanY /= valid.Count;

        var covariance = 0.0;
        var variance = 0.0;
        foreach (var sample in valid)
        {
            var dx = sample.InvW - meanX;
            covariance += dx * (sample.Depth - meanY);
            variance += dx * dx;
        }
        if (variance < 1e-12)
            return false;

        var depthB = covariance / variance;
        var depthA = meanY - depthB * meanX;

        lock (frameLock)
        {
            // Clip Z is column 3 in the System.Numerics row-vector layout.
            viewProj.M13 = (float)(depthA * r4.X);
            viewProj.M23 = (float)(depthA * r4.Y);
            viewProj.M33 = (float)(depthA * r4.Z);
            viewProj.M43 = (float)(depthA * r4.W + depthB);
            row3Calibrated = true;
        }

        if ((DateTime.UtcNow - lastCalibrationLog).TotalSeconds >= 3)
        {
            lastCalibrationLog = DateTime.UtcNow;
            log.Information($"[DxScreen] depth row calibrated from {valid.Count} scene samples "
                + $"(A={depthA:G6}, B={depthB:G6}, range={minDepth:G6}-{maxDepth:G6})");
        }
        return true;
    }

    private float? ReadDepthAt(ID3D11Texture2D depthTex, bool isD24, int x, int y)
    {
        try
        {
            var depthDesc = depthTex.Description;
            if (x < 0 || y < 0 || x >= depthDesc.Width || y >= depthDesc.Height)
                return null;

            var stagingDesc = new Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = depthDesc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            };

            using var staging = device!.CreateTexture2D(stagingDesc);
            var sourceBox = new VorticeMath.Box(x, y, 0, x + 1, y + 1, 1);
            context!.CopySubresourceRegion(staging, 0, 0, 0, 0, depthTex, 0, sourceBox);
            context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
            if (mapped.DataPointer == IntPtr.Zero)
                return null;
            try
            {
                var raw = Marshal.ReadInt32(mapped.DataPointer);
                if (isD24)
                    return (raw & 0x00FFFFFF) / (float)0x00FFFFFF;
                unsafe { return *(float*)&raw; }
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        catch
        {
            return null;
        }
    }

    private void DrawQuads(List<ScreenQuad> drawQuads, Matrix4x4 vp)
    {
        var ctx = context!;
        LogQuadDiagnostic(drawQuads[0], vp);

        // Upload vertices for this frame (4 verts per quad, world space).
        var verts = new Vertex[drawQuads.Count * 4];
        for (var q = 0; q < drawQuads.Count; q++)
        {
            var quad = drawQuads[q];

            // Match the established ImGui world-screen geometry. Camera-facing
            // world axes are computed on the game thread and handed over with
            // the quad; projection rows are not camera basis vectors.
            var right = quad.Right;
            var up = quad.Up;
            if (right.LengthSquared() > 1e-8f) right = Vector3.Normalize(right);
            if (up.LengthSquared() > 1e-8f) up = Vector3.Normalize(up);

            var (tl, tr, br, bl) = GetQuadCorners(quad, right, up);
            verts[q * 4 + 0] = new Vertex { Position = tl, Uv = new Vector2(0, 0) };
            verts[q * 4 + 1] = new Vertex { Position = tr, Uv = new Vector2(1, 0) };
            verts[q * 4 + 2] = new Vertex { Position = br, Uv = new Vector2(1, 1) };
            verts[q * 4 + 3] = new Vertex { Position = bl, Uv = new Vector2(0, 1) };
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

        // System.Numerics is row-major/row-vector, while this cbuffer is
        // column-major. Uploading the row-major bytes directly makes HLSL
        // mul(vp, columnVector) apply the equivalent transpose once.
        var depthW = capturedDepthContentW > 0
            ? capturedDepthContentW : capturedDepthTexture?.Description.Width ?? backbufferW;
        var depthH = capturedDepthContentH > 0
            ? capturedDepthContentH : capturedDepthTexture?.Description.Height ?? backbufferH;
        var cb = new Constants
        {
            ViewProj = vp,
            DepthInfo = new Vector4(
                depthW / (float)Math.Max(1, backbufferW),
                depthH / (float)Math.Max(1, backbufferH),
                reverseZ == true ? 1f : -1f,
                capturedDepthSrv != null ? 1f : 0f),
        };
        ctx.Map(constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var cmap);
        Marshal.StructureToPtr(cb, cmap.DataPointer, false);
        ctx.Unmap(constantBuffer!, 0);

        // Bind pipeline.
        var shaderDepth = capturedDepthSrv != null;
        ctx.OMSetRenderTargets(new[] { backbufferView! }, shaderDepth ? null : capturedDsvWrapper);
        ctx.OMSetDepthStencilState(shaderDepth
            ? depthStateDisabled!
            : (reverseZ == true ? depthStateReverse! : depthState!), 0);
        if (!loggedTextureWithoutDepthProbe)
        {
            loggedTextureWithoutDepthProbe = true;
            log.Information($"[DxScreen] video depth test enabled ({(reverseZ == true ? "reverse-Z" : "standard")}, "
                + $"{(shaderDepth ? "shader compare" : "hardware DSV")})");
        }
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
        ctx.PSSetConstantBuffers(0, new[] { constantBuffer! });
        ctx.PSSetSamplers(0, new[] { samplerState! });

        for (var q = 0; q < drawQuads.Count; q++)
        {
            var id = drawQuads[q].Id;
            if (!textures.TryGetValue(id, out var qt) || qt.Srv == null)
                continue;
            ctx.PSSetShaderResources(0, new[] { qt.Srv, capturedDepthSrv! });
            ctx.DrawIndexed(6, q * 6, 0);
        }

        if (worldGeometryProbeFrames > 0)
            DrawWorldGeometryProbe(ctx, drawQuads.Count);

        if (visibilityProbeFrames > 0)
            DrawVisibilityProbe(ctx);

        // The caller restores the game's output-merger target. Unbind our
        // SRVs so FFXIV can bind the retained depth texture for later work.
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
    }

    private void LogQuadDiagnostic(ScreenQuad quad, Matrix4x4 vp)
    {
        if ((DateTime.UtcNow - lastQuadDiagnosticLog).TotalSeconds < 3)
            return;
        lastQuadDiagnosticLog = DateTime.UtcNow;

        var clip = Vector4.Transform(new Vector4(quad.Center, 1f), vp);
        var source = textures.TryGetValue(quad.Id, out var qt)
            ? $"{qt.W}x{qt.H}"
            : "missing";
        if (!float.IsFinite(clip.W) || MathF.Abs(clip.W) < 1e-6f)
        {
            log.Information($"[DxScreen] quad diagnostic: source={source}, invalid clip W={clip.W}");
            return;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        var predictedDepth = clip.Z / clip.W;
        var pixelX = (ndcX + 1f) * 0.5f * backbufferW;
        var pixelY = (1f - ndcY) * 0.5f * backbufferH;
        float? sceneDepth = null;
        if (capturedDepthTexture != null && pixelX >= 0 && pixelX < backbufferW
            && pixelY >= 0 && pixelY < backbufferH)
        {
            var depthW = capturedDepthContentW > 0
                ? capturedDepthContentW : capturedDepthTexture.Description.Width;
            var depthH = capturedDepthContentH > 0
                ? capturedDepthContentH : capturedDepthTexture.Description.Height;
            var depthX = (int)(pixelX * depthW / Math.Max(1, backbufferW));
            var depthY = (int)(pixelY * depthH / Math.Max(1, backbufferH));
            sceneDepth = ReadDepthAt(capturedDepthTexture,
                IsD24DepthFormat(capturedDepthTexture.Description.Format), depthX, depthY);
        }

        var depthResult = sceneDepth.HasValue
            ? (reverseZ == true
                ? predictedDepth >= sceneDepth.Value
                : predictedDepth <= sceneDepth.Value)
            : (bool?)null;
        var right = quad.Right.LengthSquared() > 1e-8f ? Vector3.Normalize(quad.Right) : Vector3.UnitX;
        var up = quad.Up.LengthSquared() > 1e-8f ? Vector3.Normalize(quad.Up) : Vector3.UnitY;
        var corners = GetQuadCorners(quad, right, up);
        var cornerClips = new[]
        {
            Vector4.Transform(new Vector4(corners.Tl, 1f), vp),
            Vector4.Transform(new Vector4(corners.Tr, 1f), vp),
            Vector4.Transform(new Vector4(corners.Br, 1f), vp),
            Vector4.Transform(new Vector4(corners.Bl, 1f), vp),
        };
        var cornerPixels = new Vector2[4];
        var validCorners = true;
        for (var i = 0; i < cornerClips.Length; i++)
        {
            var c = cornerClips[i];
            if (!float.IsFinite(c.W) || c.W <= 1e-6f)
            {
                validCorners = false;
                continue;
            }
            cornerPixels[i] = new Vector2(
                (c.X / c.W + 1f) * 0.5f * backbufferW,
                (1f - c.Y / c.W) * 0.5f * backbufferH);
        }
        var area = 0f;
        if (validCorners)
        {
            for (var i = 0; i < 4; i++)
            {
                var a = cornerPixels[i];
                var b = cornerPixels[(i + 1) % 4];
                area += a.X * b.Y - b.X * a.Y;
            }
            area = MathF.Abs(area) * 0.5f;
        }

        log.Information($"[DxScreen] quad diagnostic: source={source}, center=({quad.Center.X:F2},"
            + $"{quad.Center.Y:F2},{quad.Center.Z:F2}), clipW={clip.W:F3}, pixel=({pixelX:F1},{pixelY:F1}), "
            + $"depth={predictedDepth:F8}, scene={(sceneDepth.HasValue ? sceneDepth.Value.ToString("F8") : "n/a")}, "
            + $"passes={(depthResult.HasValue ? depthResult.Value.ToString() : "n/a")}, area={area:F1}, "
            + $"corners=[({cornerPixels[0].X:F0},{cornerPixels[0].Y:F0},w={cornerClips[0].W:F2}),"
            + $"({cornerPixels[1].X:F0},{cornerPixels[1].Y:F0},w={cornerClips[1].W:F2}),"
            + $"({cornerPixels[2].X:F0},{cornerPixels[2].Y:F0},w={cornerClips[2].W:F2}),"
            + $"({cornerPixels[3].X:F0},{cornerPixels[3].Y:F0},w={cornerClips[3].W:F2})]");
    }

    private static (Vector3 Tl, Vector3 Tr, Vector3 Br, Vector3 Bl) GetQuadCorners(
        ScreenQuad quad, Vector3 right, Vector3 up)
    {
        var r = right * quad.HalfWidth;
        var u = up * quad.HalfHeight;
        return (quad.Center - r + u, quad.Center + r + u,
            quad.Center + r - u, quad.Center - r - u);
    }

    private void DrawWorldGeometryProbe(ID3D11DeviceContext ctx, int quadCount)
    {
        ctx.OMSetDepthStencilState(depthStateDisabled!, 0);
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        ctx.PSSetShader(visibilityProbePs!);
        for (var q = 0; q < quadCount; q++)
            ctx.DrawIndexed(6, q * 6, 0);
        worldGeometryProbeFrames--;

        if (!loggedWorldGeometryProbe)
        {
            loggedWorldGeometryProbe = true;
            log.Information("[DxScreen] world geometry probe: drawing the actual quad solid magenta "
                + "with depth disabled for 600 Present frames");
        }
    }

    private void DrawVisibilityProbe(ID3D11DeviceContext ctx)
    {
        // Fixed clip-space marker, independent of world projection, video
        // texture, and depth. If this does not appear, the Present/backbuffer
        // injection itself is not reaching the displayed image.
        var verts = new[]
        {
            new Vertex { Position = new Vector3(-0.95f, 0.95f, 0f), Uv = Vector2.Zero },
            new Vertex { Position = new Vector3(-0.65f, 0.95f, 0f), Uv = Vector2.UnitX },
            new Vertex { Position = new Vector3(-0.65f, 0.75f, 0f), Uv = Vector2.One },
            new Vertex { Position = new Vector3(-0.95f, 0.75f, 0f), Uv = Vector2.UnitY },
        };

        var handle = GCHandle.Alloc(verts, GCHandleType.Pinned);
        try
        {
            ctx.Map(vertexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
            unsafe
            {
                Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(), (void*)mapped.DataPointer,
                    verts.Length * Marshal.SizeOf<Vertex>(), verts.Length * Marshal.SizeOf<Vertex>());
            }
            ctx.Unmap(vertexBuffer!, 0);
        }
        finally
        {
            handle.Free();
        }

        var constants = new Constants { ViewProj = Matrix4x4.Identity };
        ctx.Map(constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var constantMap);
        Marshal.StructureToPtr(constants, constantMap.DataPointer, false);
        ctx.Unmap(constantBuffer!, 0);

        ctx.OMSetDepthStencilState(depthStateDisabled!, 0);
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        ctx.PSSetShader(visibilityProbePs!);
        ctx.DrawIndexed(6, 0, 0);
        visibilityProbeFrames--;

        if (!loggedVisibilityProbe)
        {
            loggedVisibilityProbe = true;
            log.Information("[DxScreen] visibility probe: drawing magenta marker for 120 Present frames");
        }
    }

    private const string VertexShaderHlsl = @"
cbuffer CB : register(b0) { float4x4 vp; float4 depthInfo; };
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
Texture2D<float> sceneDepth : register(t1);
SamplerState samp : register(s0);
cbuffer CB : register(b0) { float4x4 vp; float4 depthInfo; };
struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(PS_IN i) : SV_TARGET
{
    if (depthInfo.w > 0.5)
    {
        int2 depthPixel = int2(i.pos.xy * depthInfo.xy);
        float storedDepth = sceneDepth.Load(int3(depthPixel, 0));
        const float bias = 0.00002;
        if (depthInfo.z > 0.0)
        {
            if (i.pos.z + bias < storedDepth) discard;
        }
        else
        {
            if (i.pos.z - bias > storedDepth) discard;
        }
    }
    return tex.Sample(samp, i.uv);
}";

    private const string VisibilityProbePixelShaderHlsl = @"
struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(PS_IN i) : SV_TARGET
{
    return float4(1.0, 0.0, 1.0, 1.0);
}";

    public void Dispose()
    {
        Disable();
        presentHook?.Dispose();
        presentHook = null;

        foreach (var qt in textures.Values)
            qt.Dispose();
        textures.Clear();
        capturedDsvWrapper?.Dispose();
        capturedDepthSrv?.Dispose();
        capturedDepthTexture?.Dispose();
        backbufferView?.Dispose();
        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();
        constantBuffer?.Dispose();
        vs?.Dispose();
        ps?.Dispose();
        visibilityProbePs?.Dispose();
        layout?.Dispose();
        blendState?.Dispose();
        depthState?.Dispose();
        depthStateReverse?.Dispose();
        depthStateDisabled?.Dispose();
        rasterState?.Dispose();
        samplerState?.Dispose();
    }
}
