using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EmulatorStream;

namespace RetroXIV.Emulation;

// Managed frontend for a libretro core. Loads the native core DLL, wires up the callbacks the core
// expects, and runs the emulation on its own thread paced to the core's native FPS - keeping it off
// the game thread so a (comparatively heavy) accuracy core like bsnes cannot drag the game's
// framerate down. The latest decoded frame is exposed as RGBA32 for rendering; decoded audio is
// buffered for the audio player to drain.
public sealed class RetroCore : IDisposable, IEmulatorBackend
{
    private IntPtr library;

    // Resolved core exports.
    private RetroInitDelegate init = null!;
    private RetroDeinitDelegate deinit = null!;
    private RetroGetSystemInfoDelegate getSystemInfo = null!;
    private RetroGetSystemAvInfoDelegate getSystemAvInfo = null!;
    private RetroLoadGameDelegate loadGame = null!;
    private RetroUnloadGameDelegate unloadGame = null!;
    private RetroRunDelegate run = null!;
    private RetroResetDelegate reset = null!;
    private RetroSetEnvironmentDelegate setEnvironment = null!;
    private RetroSetVideoRefreshDelegate setVideoRefresh = null!;
    private RetroSetAudioSampleDelegate setAudioSample = null!;
    private RetroSetAudioSampleBatchDelegate setAudioSampleBatch = null!;
    private RetroSetInputPollDelegate setInputPoll = null!;
    private RetroSetInputStateDelegate setInputState = null!;
    private RetroSetControllerPortDeviceDelegate setControllerPortDevice = null!;

    // Callback delegate instances. These MUST live as fields: the native core holds raw pointers to
    // them, so if the GC collected them the next callback would jump into freed memory.
    private RetroEnvironmentDelegate environmentCb = null!;
    private RetroVideoRefreshDelegate videoCb = null!;
    private RetroAudioSampleDelegate audioCb = null!;
    private RetroAudioSampleBatchDelegate audioBatchCb = null!;
    private RetroInputPollDelegate inputPollCb = null!;
    private RetroInputStateDelegate inputStateCb = null!;
    private RetroLogPrintfDelegate logPrintfCb = null!;
    // Save-state support (retro_serialize / retro_unserialize).
    private RetroSerializeSizeDelegate serializeSize = null!;
    private RetroSerializeDelegate serialize = null!;
    private RetroUnserializeDelegate unserialize = null!;
    // Serialize all calls into the native libretro core without relying on a
    // CLR Monitor. A Monitor.Exit failure on this boundary used to escape the
    // background thread and terminate the entire game process.
    private readonly SemaphoreSlim coreGate = new(1, 1);

    private IntPtr systemDirPtr;
    private IntPtr saveDirPtr;
    private GCHandle romHandle;
    private bool romPinned;

    // Set when retro_system_info.need_fullpath is true (disc-based cores like
    // Beetle PSX): content is loaded by path only, never read into memory.
    private bool needFullpath;

    // Core option store (retro_variable). Cores declare options via
    // SET_VARIABLES ("key", "Description; opt1|opt2|...") and read them back
    // via GET_VARIABLE during init/load. There is no options UI yet, so every
    // value stays at its default (the first listed option); answering the
    // queries is still mandatory for cores like Beetle PSX to load at all.
    // The values are pinned unmanaged strings because the core receives raw
    // pointers into them.
    private readonly Dictionary<string, IntPtr> variableValues = new();
    private readonly Dictionary<string, string> variableRaws = new();

    // Per-option overrides for cores whose first-listed default does not fit
    // this frontend. LRPS2 defaults its renderer to Auto and expects a complete
    // hardware-rendering callback bridge. Keep the stable CPU path as the beta
    // default until that bridge is fully wired.
    private static readonly Dictionary<string, string> builtinOverrides = new()
    {
        ["pcsx2_renderer"] = "Software (SW)",
        // Beetle PSX ships DualShock support off ("disabled"). Boot the pad
        // in analog mode so analog games see the sticks; the core's toggle
        // combo (L1+R1+Select) still flips it back to digital mid-game.
        ["beetle_psx_analog_toggle"] = "enabled-analog",
    };

    private readonly Dictionary<string, string> variableOverrides = new(builtinOverrides);

    // Aspect ratios for cores that report square pixels (aspect 0). Without
    // this they fall back to the legacy 3:2 screens, which stretches the
    // Game Boy's 160x144 panel.
    private static readonly (string Library, float Aspect)[] builtinAspects =
    {
        ("Gambatte", 10f / 9f),
        // The NES panel is 256x240 with 8:7 pixels; keep it from stretching
        // to the legacy 3:2 world-screen fallback when the core reports 0.
        ("FCEUmm", 8f / 7f),
    };

    // Frontend-set option values (future options UI, diagnostics). Applied at
    // the next SET_VARIABLES; unknown keys are kept and matched later.
    public void OverrideVariable(string key, string value) => variableOverrides[key] = value;

    private Thread? thread;
    private volatile bool running;

    // How long to wait for the emulation thread to leave native code during
    // teardown before giving up on an orderly unload.
    private const int TeardownJoinTimeoutMs = 3000;

    // Set when the emulation thread did not stop in time. The core and its
    // native library are deliberately left loaded for the rest of the process
    // lifetime, because freeing anything under a live retro_run thread would
    // crash the game.
    private bool teardownFailed;
    public bool TeardownFailed => teardownFailed;

    // Set to pause/resume the emulation (e.g. when the TV screen is powered off).
    public volatile bool Paused;

    // Measured actual emulation frame rate (for the FPS overlay).
    public double EmulationFps { get; private set; }
    private long fpsFrames;
    private double fpsTime;

    // Negotiated / core state.
    public string LibraryName { get; private set; } = string.Empty;
    public string LibraryVersion { get; private set; } = string.Empty;
    public string[] Extensions { get; private set; } = [];
    public bool IsGameLoaded { get; private set; }
    public bool ShutdownRequested { get; private set; }
    public int BaseWidth { get; private set; }
    public int BaseHeight { get; private set; }
    public double Fps { get; private set; } = 60.0;
    public double SampleRate { get; private set; } = 32000.0;
    private uint pixelFormat = Libretro.PixelFormat0RGB1555;

    // Display aspect (width / height) declared by the core, 0 when the core
    // did not declare one and the frontend should apply its own default.
    public double AspectRatio { get; private set; }

    // Lockstep netplay is only validated for the deterministic bsnes core,
    // and the 16-bit SNES-only input protocol is wrong for other platforms.
    public bool SupportsNetplay =>
        LibraryName.Contains("bsnes", StringComparison.OrdinalIgnoreCase);

    // Raw SET_VARIABLES declarations ("Description; opt1|opt2|...") keyed by
    // option key, for diagnostics (tools/core-smoke -info).
    public IReadOnlyDictionary<string, string> VariableDeclarations => variableRaws;

    // The value currently answered for a core option (for diagnostics).
    public string? GetVariableSelection(string key) =>
        variableValues.TryGetValue(key, out var ptr) ? Marshal.PtrToStringAnsi(ptr) : null;

    // Invoked on the emulation thread immediately before each frame, to refresh input.
    public Action? PreFrame { get; set; }

    // Background-thread failures must be reported, never allowed to reach
    // AppDomain.UnhandledException (Dalamud treats that as a process crash).
    public Action<Exception>? BackgroundError { get; set; }

    // Raised when teardown has to leave the core loaded because the emulation
    // thread would not stop; informational, the process is deliberately kept alive.
    public Action<string>? TeardownWarning { get; set; }

    // Core log lines (retro_log_level, text), emitted from the emulation
    // thread.
    public Action<int, string>? LogReceived { get; set; }

    // Diagnostic: every environment query and whether we answered it.
    public Action<uint, bool>? EnvironmentTrace { get; set; }

    // Hardware presentation context for cores that render on the GPU (LRPS2).
    // When a core's SET_HW_RENDER is accepted, frames are read back from this
    // context after each retro_run instead of arriving via video refresh.
    public IHwRenderContext? HwRender { get; set; }

    private RetroHwContextResetDelegate? hwContextReset;
    private RetroHwContextResetDelegate? hwContextDestroy;
    private volatile bool hwActive;
    private RetroCoreOptionsUpdateDisplayDelegate? optionVisibilityCallback;

    // CRT vsnprintf, used to format the core's va_list log lines natively.
    private static readonly VsnprintfDelegate? vsnprintfCall = ResolveVsnprintf();

    private delegate int VsnprintfDelegate(IntPtr buffer, UIntPtr count, IntPtr format, IntPtr vaList);

    private static VsnprintfDelegate? ResolveVsnprintf()
    {
        if (NativeLibrary.TryLoad("ucrtbase.dll", out var ucrt) &&
            NativeLibrary.TryGetExport(ucrt, "vsnprintf", out var export))
        {
            return Marshal.GetDelegateForFunctionPointer<VsnprintfDelegate>(export);
        }

        return null;
    }

    // Latest frame, RGBA32.
    private readonly object frameLock = new();
    private byte[] frame = Array.Empty<byte>();
    private int frameWidth;
    private int frameHeight;
    private long frameVersion;

    // Audio ring buffer (interleaved stereo int16).
    private readonly object audioLock = new();
    private readonly short[] audioBuffer = new short[1 << 16];
    private int audioWrite;
    private int audioRead;
    private int audioCount;

    // Separate tap buffer for streaming — WriteAudio fills both, so the
    // stream sender never steals samples from local playback.
    private readonly short[] streamBuffer = new short[1 << 16];
    private int streamWrite;
    private int streamRead;
    private int streamCount;

    public Func<uint, uint, uint, uint, short>? InputState { get; set; }

    public string SystemDirectory { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;
    public string? LastLoadError { get; private set; }

    public void Load(string corePath)
    {
        if (library != IntPtr.Zero)
        {
            throw new InvalidOperationException("A core is already loaded.");
        }

        library = NativeLibrary.Load(corePath);

        init = Resolve<RetroInitDelegate>("retro_init");
        deinit = Resolve<RetroDeinitDelegate>("retro_deinit");
        getSystemInfo = Resolve<RetroGetSystemInfoDelegate>("retro_get_system_info");
        getSystemAvInfo = Resolve<RetroGetSystemAvInfoDelegate>("retro_get_system_av_info");
        loadGame = Resolve<RetroLoadGameDelegate>("retro_load_game");
        unloadGame = Resolve<RetroUnloadGameDelegate>("retro_unload_game");
        run = Resolve<RetroRunDelegate>("retro_run");
        reset = Resolve<RetroResetDelegate>("retro_reset");
        setEnvironment = Resolve<RetroSetEnvironmentDelegate>("retro_set_environment");
        setVideoRefresh = Resolve<RetroSetVideoRefreshDelegate>("retro_set_video_refresh");
        setAudioSample = Resolve<RetroSetAudioSampleDelegate>("retro_set_audio_sample");
        setAudioSampleBatch = Resolve<RetroSetAudioSampleBatchDelegate>("retro_set_audio_sample_batch");
        setInputPoll = Resolve<RetroSetInputPollDelegate>("retro_set_input_poll");
        setInputState = Resolve<RetroSetInputStateDelegate>("retro_set_input_state");
        setControllerPortDevice = Resolve<RetroSetControllerPortDeviceDelegate>("retro_set_controller_port_device");
        serializeSize = Resolve<RetroSerializeSizeDelegate>("retro_serialize_size");
        serialize = Resolve<RetroSerializeDelegate>("retro_serialize");
        unserialize = Resolve<RetroUnserializeDelegate>("retro_unserialize");

        environmentCb = Environment;
        videoCb = VideoRefresh;
        audioCb = AudioSample;
        audioBatchCb = AudioSampleBatch;
        inputPollCb = InputPoll;
        inputStateCb = InputStateCallback;
        logPrintfCb = LogPrintf;
        systemDirPtr = Marshal.StringToHGlobalAnsi(SystemDirectory);
        saveDirPtr = Marshal.StringToHGlobalAnsi(SaveDirectory);

        setEnvironment(environmentCb);
        setVideoRefresh(videoCb);
        setAudioSample(audioCb);
        setAudioSampleBatch(audioBatchCb);
        setInputPoll(inputPollCb);
        setInputState(inputStateCb);

        init();

        getSystemInfo(out var info);
        LibraryName = Marshal.PtrToStringAnsi(info.LibraryName) ?? string.Empty;
        LibraryVersion = Marshal.PtrToStringAnsi(info.LibraryVersion) ?? string.Empty;
        needFullpath = info.NeedFullpath;
        Extensions = (Marshal.PtrToStringAnsi(info.ValidExtensions) ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool LoadGame(string romPath)
    {
        if (library == IntPtr.Zero)
        {
            throw new InvalidOperationException("No core loaded.");
        }

        if (teardownFailed)
        {
            LastLoadError = "The previous session did not stop cleanly; restart the game to use this core again.";
            return false;
        }

        UnloadGame();
        LastLoadError = null;

        if (!ValidatePs2ContentPrerequisites(romPath, out var prerequisiteError))
        {
            LastLoadError = prerequisiteError;
            return false;
        }

        var pathPtr = Marshal.StringToHGlobalAnsi(romPath);
        try
        {
            RetroGameInfo gameInfo;
            if (needFullpath)
            {
                // Disc images (cue/bin, pbp, chd, m3u) are read from disk by
                // the core itself; they can reference sibling files and are
                // far too large to copy into managed memory.
                gameInfo = new RetroGameInfo
                {
                    Path = pathPtr,
                    Data = IntPtr.Zero,
                    Size = UIntPtr.Zero,
                    Meta = IntPtr.Zero,
                };
            }
            else
            {
                var rom = File.ReadAllBytes(romPath);
                romHandle = GCHandle.Alloc(rom, GCHandleType.Pinned);
                romPinned = true;

                gameInfo = new RetroGameInfo
                {
                    Path = pathPtr,
                    Data = romHandle.AddrOfPinnedObject(),
                    Size = (UIntPtr)rom.Length,
                    Meta = IntPtr.Zero,
                };
            }

            if (!loadGame(ref gameInfo))
            {
                LastLoadError = "The native core refused to load this content.";
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }

        CaptureAvInfo();
        // LRPS2 starts its native CPU thread from retro_load_game(), but opens
        // the GS device from the first retro_run().  Keep that transition
        // adjacent: content boot can otherwise reach the native GS loop on a
        // worker thread before the frontend has opened the device.
        RunFrame();
        return true;
    }

    private bool ValidatePs2ContentPrerequisites(string contentPath, out string error)
    {
        error = string.Empty;
        var isPs2 = LibraryName.Contains("LRPS2", StringComparison.OrdinalIgnoreCase)
            || LibraryName.Contains("PCSX2", StringComparison.OrdinalIgnoreCase);
        var extension = Path.GetExtension(contentPath).ToLowerInvariant();
        var isDiscImage = extension is ".iso" or ".ciso" or ".cue" or ".bin" or ".gz"
            or ".chd" or ".cso" or ".zso" or ".mdf" or ".nrg" or ".dump" or ".img" or ".m3u";
        if (!isPs2 || !isDiscImage)
        {
            return true;
        }

        // LRPS2 accepts a standalone 4-8 MB main BIOS. ROM1/ROM2/EROM/NVM
        // companions are optional; when supplied, LRPS2 matches them by the
        // main BIOS basename. Do not reject a valid single-file BIOS or demand
        // unrelated generic companion names here.
        var biosDirectory = Path.Combine(SystemDirectory, "pcsx2", "bios");
        if (!Directory.Exists(biosDirectory))
        {
            error = $"PS2 BIOS folder is missing: {biosDirectory}";
            return false;
        }

        var hasMainBios = Directory.EnumerateFiles(biosDirectory)
            .Select(path => new FileInfo(path).Length)
            .Any(length => length >= 4 * 1024 * 1024 && length <= 8 * 1024 * 1024);
        if (hasMainBios)
        {
            return true;
        }

        error = "PS2 BIOS folder contains no 4-8 MB main BIOS dump. Add a legal PS2 BIOS to "
            + $"{biosDirectory}; optional companion dumps must share its basename.";
        return false;
    }

    // Boot without content (cores that advertised no-game support, e.g. the
    // PS2 BIOS browser).
    public bool LoadNoGame()
    {
        if (library == IntPtr.Zero)
        {
            throw new InvalidOperationException("No core loaded.");
        }

        if (teardownFailed)
        {
            return false;
        }

        UnloadGame();

        RetroGameInfo gameInfo = default;
        if (!loadGame(ref gameInfo))
        {
            return false;
        }

        CaptureAvInfo();
        // Keep no-content BIOS boot on the same lifecycle as content boot.
        RunFrame();
        return true;
    }

    private void CaptureAvInfo()
    {
        getSystemAvInfo(out var av);
        BaseWidth = (int)av.Geometry.BaseWidth;
        BaseHeight = (int)av.Geometry.BaseHeight;
        Fps = av.Timing.Fps > 0 ? av.Timing.Fps : 60.0;
        SampleRate = av.Timing.SampleRate > 0 ? av.Timing.SampleRate : 32000.0;
        AspectRatio = av.Geometry.AspectRatio > 0f
            ? av.Geometry.AspectRatio
            : LookupBuiltinAspect();
        ShutdownRequested = false;
        IsGameLoaded = true;

        // The GS of a hardware-rendering core only opens once the frontend
        // signals that its context is live.
        hwContextReset?.Invoke();
    }

    // Canonical display aspect for square-pixel cores that report 0. Returns
    // 0 when no entry matches, keeping the legacy 3:2 screen fallback.
    private float LookupBuiltinAspect()
    {
        foreach (var (library, aspect) in builtinAspects)
        {
            if (LibraryName.Contains(library, StringComparison.OrdinalIgnoreCase))
            {
                return aspect;
            }
        }

        return 0f;
    }

    // Begin running the emulation on its own thread.
    public void Start()
    {
        // A poisoned core still has a thread inside native code; starting
        // another would run two retro_run callers against the same core.
        if (running || teardownFailed)
        {
            return;
        }

        running = true;
        thread = new Thread(RunLoop) { IsBackground = true, Name = "RetroXIV.Core" };
        thread.Start();
    }

    public void UnloadGame()
    {
        if (teardownFailed)
        {
            return;
        }

        running = false;
        if (thread?.Join(TeardownJoinTimeoutMs) == false)
        {
            // The emulation thread is still inside native core code (a long
            // frame, a stalled netplay send). Tearing down under it would
            // crash the game, so leave the core loaded and surface it.
            teardownFailed = true;
            TeardownWarning?.Invoke(
                "The emulator thread did not stop in time; the core was left loaded to avoid crashing the game. Restart the game to unload it fully.");
            return;
        }

        thread = null;

        if (IsGameLoaded)
        {
            unloadGame();
            IsGameLoaded = false;
        }

        if (hwActive)
        {
            hwContextDestroy?.Invoke();
            hwActive = false;
        }

        if (romPinned)
        {
            romHandle.Free();
            romPinned = false;
        }
    }

    public void Reset()
    {
        coreGate.Wait();
        try
        {
            reset();
        }
        finally
        {
            coreGate.Release();
        }
    }

    // Advance the emulation by one frame on the calling thread (used to pre-fill audio before the
    // emulation thread starts).
    public void RunFrame()
    {
        coreGate.Wait();
        try
        {
            if (IsGameLoaded)
            {
                RunCoreFrame();
            }
        }
        finally
        {
            coreGate.Release();
        }
    }

    // Hardware-rendering cores present into the frontend's context instead of
    // calling video refresh: bind the target, run, read the frame back.
    private void RunCoreFrame()
    {
        if (hwActive && HwRender != null)
        {
            HwRender.BeginFrame(BaseWidth, BaseHeight);
            run();
            if (HwRender.TryReadFrame(out var rgba, out var w, out var h))
            {
                PushFrame(rgba, w, h);
            }
        }
        else
        {
            run();
        }
    }

    // Capture the current emulation state as a byte array (thread-safe vs. the emulation thread).
    public byte[]? SaveState()
    {
        if (!IsGameLoaded)
        {
            return null;
        }

        coreGate.Wait();
        try
        {
            var size = (int)serializeSize().ToUInt32();
            if (size <= 0)
            {
                return null;
            }

            var buffer = new byte[size];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return serialize(handle.AddrOfPinnedObject(), (UIntPtr)size) ? buffer : null;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            coreGate.Release();
        }
    }

    // Restore a previously captured state (thread-safe vs. the emulation thread).
    public bool LoadState(byte[] data)
    {
        if (!IsGameLoaded || data.Length == 0)
        {
            return false;
        }

        coreGate.Wait();
        try
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return unserialize(handle.AddrOfPinnedObject(), (UIntPtr)data.Length);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            coreGate.Release();
        }
    }

    private void RunLoop()
    {
        try
        {
            RunLoopCore();
        }
        catch (Exception ex)
        {
            running = false;
            try
            {
                BackgroundError?.Invoke(ex);
            }
            catch
            {
                // An error reporter must never turn a contained emulator
                // failure back into an unhandled process-wide exception.
            }
        }
    }

    private void RunLoopCore()
    {
        var frameTime = 1.0 / Fps;
        var stopwatch = Stopwatch.StartNew();
        var next = frameTime;
        var prev = 0.0;

        while (running)
        {
            if (Paused)
            {
                Thread.Sleep(16);
                prev = stopwatch.Elapsed.TotalSeconds;
                next = prev + frameTime;
                continue;
            }

            coreGate.Wait();
            try
            {
                PreFrame?.Invoke();
                if (IsGameLoaded)
                {
                    RunCoreFrame();
                }
            }
            finally
            {
                coreGate.Release();
            }

            var now = stopwatch.Elapsed.TotalSeconds;
            var delta = now - prev;
            prev = now;

            // Measure the actual emulation frame rate over a short window.
            fpsFrames++;
            fpsTime += delta;
            if (fpsTime >= 0.5)
            {
                EmulationFps = fpsFrames / fpsTime;
                fpsFrames = 0;
                fpsTime = 0;
            }

            if (now < next)
            {
                Thread.Sleep(Math.Max(1, (int)((next - now) * 1000)));
            }

            next += frameTime;

            // Resync if we fall far behind (e.g. a hitch) to avoid a burst of catch-up frames.
            if (next - stopwatch.Elapsed.TotalSeconds < -0.1)
            {
                next = stopwatch.Elapsed.TotalSeconds + frameTime;
            }
        }
    }

    public long FrameVersion
    {
        get { lock (frameLock) { return frameVersion; } }
    }

    public bool TryGetFrame(out byte[] rgba, out int width, out int height)
    {
        lock (frameLock)
        {
            if (frameVersion == 0 || frame.Length == 0)
            {
                rgba = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }

            rgba = (byte[])frame.Clone();
            width = frameWidth;
            height = frameHeight;
            return true;
        }
    }

    public int ReadAudio(short[] destination, int maxFrames)
    {
        lock (audioLock)
        {
            var frames = Math.Min(maxFrames, audioCount / 2);
            for (var i = 0; i < frames * 2; i++)
            {
                destination[i] = audioBuffer[audioRead];
                audioRead = (audioRead + 1) % audioBuffer.Length;
            }

            audioCount -= frames * 2;
            return frames;
        }
    }

    public int BufferedAudioFrames
    {
        get { lock (audioLock) { return audioCount / 2; } }
    }

    private T Resolve<T>(string name) where T : Delegate
    {
        var ptr = NativeLibrary.GetExport(library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    // --- libretro callbacks (invoked on the emulation thread during run) ---

    private bool Environment(uint cmd, IntPtr data)
    {
        var handled = EnvironmentCore(cmd, data);
        EnvironmentTrace?.Invoke(cmd, handled);
        return handled;
    }

    private bool EnvironmentCore(uint cmd, IntPtr data)
    {
        switch (cmd)
        {
            case Libretro.EnvSetPixelFormat:
                pixelFormat = (uint)Marshal.ReadInt32(data);
                return true;

            case Libretro.EnvGetCanDupe:
                Marshal.WriteByte(data, 1);
                return true;

            case Libretro.EnvGetSystemDirectory:
                Marshal.WriteIntPtr(data, systemDirPtr);
                return true;

            case Libretro.EnvGetSaveDirectory:
                Marshal.WriteIntPtr(data, saveDirPtr);
                return true;

            case Libretro.EnvGetVariableUpdate:
                Marshal.WriteByte(data, 0);
                return true;

            case Libretro.EnvSetVariables:
                SetVariables(data);
                return true;

            case Libretro.EnvGetVariable:
            {
                var keyPtr = Marshal.ReadIntPtr(data);
                var key = Marshal.PtrToStringAnsi(keyPtr);
                if (key != null && variableValues.TryGetValue(key, out var valuePtr))
                {
                    Marshal.WriteIntPtr(data, IntPtr.Size, valuePtr);
                    return true;
                }

                return false;
            }

            case Libretro.EnvSetGeometry:
            {
                // The core changed its video mode mid-game (PS1 titles switch
                // resolutions freely). Keep the base dimensions current; an
                // explicit aspect overrides the load-time one.
                var geometry = Marshal.PtrToStructure<RetroGameGeometry>(data);
                ApplyGeometry(geometry);
                return true;
            }

            case Libretro.EnvSetSystemAvInfo:
            {
                // Full AV renegotiation (LRPS2 reports PS2 video-mode changes
                // this way). Same geometry semantics as SET_GEOMETRY, plus
                // timing: the run loop and audio resampler follow the new
                // rate on their next cycle.
                var av = Marshal.PtrToStructure<RetroSystemAvInfo>(data);
                ApplyGeometry(av.Geometry);
                if (av.Timing.Fps > 0)
                {
                    Fps = av.Timing.Fps;
                }

                if (av.Timing.SampleRate > 0)
                {
                    SampleRate = av.Timing.SampleRate;
                }

                return true;
            }

            case Libretro.EnvGetLogInterface:
                Marshal.StructureToPtr(new RetroLogCallback { Log = logPrintfCb }, data, false);
                return true;

            case Libretro.EnvSetInputDescriptors:
                // The PS2 core uses the standard joypad callbacks and does not
                // require per-button descriptor metadata.
                return true;

            case Libretro.EnvSetCoreOptionsDisplay:
                // Dynamic option visibility is UI-only for this frontend; the
                // core still owns and reads the option values.
                return true;

            case Libretro.EnvSetCoreOptionsUpdateDisplayCallback:
            {
                var callback = Marshal.PtrToStructure<RetroCoreOptionsUpdateDisplayCallback>(data);
                optionVisibilityCallback = callback.Callback;
                return optionVisibilityCallback != null;
            }

            case Libretro.EnvSetHwRender:
            {
                // Without a provider this frontend stays software-frame only
                // and cores must use their CPU renderer. Accepting without a
                // real context (as a misparsed geometry call once did) sends
                // LRPS2 down a hardware path that crashes on the missing GS.
                var hwCb = Marshal.PtrToStructure<RetroHwRenderCallback>(data);
                if (HwRender == null || hwCb.ContextType != HwRender.ContextType)
                {
                    return false;
                }

                hwContextReset = Marshal.GetDelegateForFunctionPointer<RetroHwContextResetDelegate>(hwCb.ContextReset);
                hwContextDestroy = hwCb.ContextDestroy != IntPtr.Zero
                    ? Marshal.GetDelegateForFunctionPointer<RetroHwContextResetDelegate>(hwCb.ContextDestroy)
                    : null;
                hwActive = true;
                return true;
            }

            case Libretro.EnvGetHwRenderInterface:
                return hwActive && HwRender != null && HwRender.FillInterface(data);

            case Libretro.EnvGetPreferredHwRender:
                if (HwRender == null)
                {
                    return false;
                }

                Marshal.WriteInt32(data, HwRender.ContextType);
                return true;

            case Libretro.EnvSetRotation:
            case Libretro.EnvSetMessage:
            case Libretro.EnvSetSupportNoGame:
                return true;

            case Libretro.EnvShutdown:
                ShutdownRequested = true;
                return true;

            default:
                return false;
        }
    }

    private void ApplyGeometry(RetroGameGeometry geometry)
    {
        BaseWidth = (int)geometry.BaseWidth;
        BaseHeight = (int)geometry.BaseHeight;
        if (geometry.AspectRatio > 0f)
        {
            AspectRatio = geometry.AspectRatio;
        }
    }

    // Parses the SET_VARIABLES array (retro_variable pairs terminated by a
    // null key). Each value string has the form "Description; opt1|opt2|...";
    // the first option is the default. Values are pinned for the lifetime of
    // the core because GET_VARIABLE hands raw pointers to the native side.
    private void SetVariables(IntPtr data)
    {
        FreeVariables();

        var offset = 0;
        while (true)
        {
            var keyPtr = Marshal.ReadIntPtr(data, offset);
            if (keyPtr == IntPtr.Zero)
            {
                break;
            }

            var rawPtr = Marshal.ReadIntPtr(data, offset + IntPtr.Size);
            offset += IntPtr.Size * 2;

            var key = Marshal.PtrToStringAnsi(keyPtr);
            var raw = Marshal.PtrToStringAnsi(rawPtr) ?? string.Empty;
            if (string.IsNullOrEmpty(key) || variableValues.ContainsKey(key))
            {
                continue;
            }

            variableRaws[key] = raw;

            var defaultValue = string.Empty;
            var separator = raw.IndexOf(';');
            if (separator >= 0)
            {
                var options = raw[(separator + 1)..].Split('|', StringSplitOptions.TrimEntries);
                if (options.Length > 0)
                {
                    defaultValue = options[0];
                }

                if (variableOverrides.TryGetValue(key, out var overridden) &&
                    Array.IndexOf(options, overridden) >= 0)
                {
                    defaultValue = overridden;
                }
            }

            variableValues[key] = Marshal.StringToHGlobalAnsi(defaultValue);
        }
    }

    private void FreeVariables()
    {
        foreach (var ptr in variableValues.Values)
        {
            Marshal.FreeHGlobal(ptr);
        }

        variableValues.Clear();
        variableRaws.Clear();
    }

    private unsafe void VideoRefresh(IntPtr data, uint width, uint height, UIntPtr pitch)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var w = (int)width;
        var h = (int)height;
        var pitchBytes = (int)pitch.ToUInt32();
        var rgba = new byte[w * h * 4];
        var src = (byte*)data;

        for (var y = 0; y < h; y++)
        {
            ConvertRow(src + y * pitchBytes, rgba, y * w * 4, w, pixelFormat);
        }

        PushFrame(rgba, w, h);
    }

    private void PushFrame(byte[] rgba, int w, int h)
    {
        lock (frameLock)
        {
            frame = rgba;
            frameWidth = w;
            frameHeight = h;
            frameVersion++;
        }
    }

    private static unsafe void ConvertRow(byte* row, byte[] dst, int dstOffset, int width, uint format)
    {
        switch (format)
        {
            case Libretro.PixelFormatXRGB8888:
            {
                var src = (uint*)row;
                for (var x = 0; x < width; x++)
                {
                    var p = src[x];
                    var o = dstOffset + x * 4;
                    dst[o] = (byte)((p >> 16) & 0xFF);
                    dst[o + 1] = (byte)((p >> 8) & 0xFF);
                    dst[o + 2] = (byte)(p & 0xFF);
                    dst[o + 3] = 0xFF;
                }
                break;
            }

            case Libretro.PixelFormatRGB565:
            {
                var src = (ushort*)row;
                for (var x = 0; x < width; x++)
                {
                    var p = src[x];
                    var r5 = (p >> 11) & 0x1F;
                    var g6 = (p >> 5) & 0x3F;
                    var b5 = p & 0x1F;
                    var o = dstOffset + x * 4;
                    dst[o] = (byte)((r5 << 3) | (r5 >> 2));
                    dst[o + 1] = (byte)((g6 << 2) | (g6 >> 4));
                    dst[o + 2] = (byte)((b5 << 3) | (b5 >> 2));
                    dst[o + 3] = 0xFF;
                }
                break;
            }

            default: // 0RGB1555 (bsnes default)
            {
                var src = (ushort*)row;
                for (var x = 0; x < width; x++)
                {
                    var p = src[x];
                    var r5 = (p >> 10) & 0x1F;
                    var g5 = (p >> 5) & 0x1F;
                    var b5 = p & 0x1F;
                    var o = dstOffset + x * 4;
                    dst[o] = (byte)((r5 << 3) | (r5 >> 2));
                    dst[o + 1] = (byte)((g5 << 3) | (g5 >> 2));
                    dst[o + 2] = (byte)((b5 << 3) | (b5 >> 2));
                    dst[o + 3] = 0xFF;
                }
                break;
            }
        }
    }

    private void AudioSample(short left, short right)
    {
        WriteAudio(left);
        WriteAudio(right);
    }

    private unsafe UIntPtr AudioSampleBatch(IntPtr data, UIntPtr frames)
    {
        var count = (int)frames.ToUInt32();
        var src = (short*)data;
        for (var i = 0; i < count * 2; i++)
        {
            WriteAudio(src[i]);
        }

        return frames;
    }

    private void WriteAudio(short sample)
    {
        lock (audioLock)
        {
            if (audioCount >= audioBuffer.Length)
            {
                audioRead = (audioRead + 1) % audioBuffer.Length;
                audioCount--;
            }

            audioBuffer[audioWrite] = sample;
            audioWrite = (audioWrite + 1) % audioBuffer.Length;
            audioCount++;

            // Stream tap (overflow drops oldest, same as playback).
            if (streamCount >= streamBuffer.Length)
            {
                streamRead = (streamRead + 1) % streamBuffer.Length;
                streamCount--;
            }

            streamBuffer[streamWrite] = sample;
            streamWrite = (streamWrite + 1) % streamBuffer.Length;
            streamCount++;
        }
    }

    // Drain the stream tap buffer (for the network sender).  Does NOT
    // touch the playback buffer.
    public int ReadStreamAudio(short[] destination, int maxFrames)
    {
        lock (audioLock)
        {
            var frames = Math.Min(maxFrames, streamCount / 2);
            for (var i = 0; i < frames * 2; i++)
            {
                destination[i] = streamBuffer[streamRead];
                streamRead = (streamRead + 1) % streamBuffer.Length;
            }

            streamCount -= frames * 2;
            return frames;
        }
    }

    private void InputPoll()
    {
    }

    private short InputStateCallback(uint port, uint device, uint index, uint id)
    {
        return InputState?.Invoke(port, device, index, id) ?? 0;
    }

    // Selects the controller type plugged into a port. Analog-capable cores
    // (PS1/PS2) need RETRO_DEVICE_ANALOG to see the sticks at all; digital
    // consoles keep the plain RETRO_DEVICE_JOYPAD. Call after LoadGame —
    // RetroArch makes the same call after retro_load_game.
    public void SetControllerPortDevice(uint port, uint device)
    {
        if (library == IntPtr.Zero)
        {
            return;
        }

        setControllerPortDevice(port, device);
    }

    // Formats the core's printf-style log line natively (the va_list pointer
    // cannot be interpreted managed-side) and forwards it.
    private void LogPrintf(int level, IntPtr format, IntPtr vaList)
    {
        var sink = LogReceived;
        if (sink == null || vsnprintfCall == null)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            vsnprintfCall(buffer, (UIntPtr)4096, format, vaList);
            var text = Marshal.PtrToStringAnsi(buffer)?.TrimEnd() ?? string.Empty;
            if (text.Length > 0)
            {
                sink(level, text);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        UnloadGame();

        if (teardownFailed)
        {
            // The emulation thread is presumed alive inside the native core:
            // leave the library mapped and every pointer it may hand the core
            // allocated. The process is deliberately kept alive with the core
            // loaded rather than crashing on a use-after-free.
            return;
        }

        FreeVariables();

        if (library != IntPtr.Zero)
        {
            deinit();
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
        }

        if (systemDirPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(systemDirPtr);
            systemDirPtr = IntPtr.Zero;
        }

        if (saveDirPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(saveDirPtr);
            saveDirPtr = IntPtr.Zero;
        }
    }
}
