using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EmulatorStream;

namespace SnesEmulator.Emulation;

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

    // Callback delegate instances. These MUST live as fields: the native core holds raw pointers to
    // them, so if the GC collected them the next callback would jump into freed memory.
    private RetroEnvironmentDelegate environmentCb = null!;
    private RetroVideoRefreshDelegate videoCb = null!;
    private RetroAudioSampleDelegate audioCb = null!;
    private RetroAudioSampleBatchDelegate audioBatchCb = null!;
    private RetroInputPollDelegate inputPollCb = null!;
    private RetroInputStateDelegate inputStateCb = null!;

    // Save-state support (retro_serialize / retro_unserialize).
    private RetroSerializeSizeDelegate serializeSize = null!;
    private RetroSerializeDelegate serialize = null!;
    private RetroUnserializeDelegate unserialize = null!;
    private readonly object coreLock = new();

    private IntPtr systemDirPtr;
    private IntPtr saveDirPtr;
    private GCHandle romHandle;
    private bool romPinned;

    private Thread? thread;
    private volatile bool running;

    // Set to pause/resume the emulation (e.g. when the TV screen is powered off).
    public volatile bool Paused;

    // Measured actual emulation frame rate (for the FPS overlay).
    public double EmulationFps { get; private set; }
    private long fpsFrames;
    private double fpsTime;

    // Negotiated / core state.
    public string LibraryName { get; private set; } = string.Empty;
    public string LibraryVersion { get; private set; } = string.Empty;
    public bool IsGameLoaded { get; private set; }
    public bool ShutdownRequested { get; private set; }
    public int BaseWidth { get; private set; }
    public int BaseHeight { get; private set; }
    public double Fps { get; private set; } = 60.0;
    public double SampleRate { get; private set; } = 32000.0;
    private uint pixelFormat = Libretro.PixelFormat0RGB1555;

    // Invoked on the emulation thread immediately before each frame, to refresh input.
    public Action? PreFrame { get; set; }

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
        serializeSize = Resolve<RetroSerializeSizeDelegate>("retro_serialize_size");
        serialize = Resolve<RetroSerializeDelegate>("retro_serialize");
        unserialize = Resolve<RetroUnserializeDelegate>("retro_unserialize");

        environmentCb = Environment;
        videoCb = VideoRefresh;
        audioCb = AudioSample;
        audioBatchCb = AudioSampleBatch;
        inputPollCb = InputPoll;
        inputStateCb = InputStateCallback;

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
    }

    public bool LoadGame(string romPath)
    {
        if (library == IntPtr.Zero)
        {
            throw new InvalidOperationException("No core loaded.");
        }

        UnloadGame();

        var rom = File.ReadAllBytes(romPath);
        romHandle = GCHandle.Alloc(rom, GCHandleType.Pinned);
        romPinned = true;

        var pathPtr = Marshal.StringToHGlobalAnsi(romPath);
        try
        {
            var gameInfo = new RetroGameInfo
            {
                Path = pathPtr,
                Data = romHandle.AddrOfPinnedObject(),
                Size = (UIntPtr)rom.Length,
                Meta = IntPtr.Zero,
            };

            if (!loadGame(ref gameInfo))
            {
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }

        getSystemAvInfo(out var av);
        BaseWidth = (int)av.Geometry.BaseWidth;
        BaseHeight = (int)av.Geometry.BaseHeight;
        Fps = av.Timing.Fps > 0 ? av.Timing.Fps : 60.0;
        SampleRate = av.Timing.SampleRate > 0 ? av.Timing.SampleRate : 32000.0;
        ShutdownRequested = false;
        IsGameLoaded = true;
        return true;
    }

    // Begin running the emulation on its own thread.
    public void Start()
    {
        if (running)
        {
            return;
        }

        running = true;
        thread = new Thread(RunLoop) { IsBackground = true, Name = "SnesEmulator.Core" };
        thread.Start();
    }

    public void UnloadGame()
    {
        running = false;
        thread?.Join(1000);
        thread = null;

        if (IsGameLoaded)
        {
            unloadGame();
            IsGameLoaded = false;
        }

        if (romPinned)
        {
            romHandle.Free();
            romPinned = false;
        }
    }

    public void Reset() => reset();

    // Advance the emulation by one frame on the calling thread (used to pre-fill audio before the
    // emulation thread starts).
    public void RunFrame()
    {
        if (IsGameLoaded)
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

        lock (coreLock)
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
    }

    // Restore a previously captured state (thread-safe vs. the emulation thread).
    public bool LoadState(byte[] data)
    {
        if (!IsGameLoaded || data.Length == 0)
        {
            return false;
        }

        lock (coreLock)
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
    }

    private void RunLoop()
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

            lock (coreLock)
            {
                PreFrame?.Invoke();
                if (IsGameLoaded)
                {
                    run();
                }
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

    public void Dispose()
    {
        UnloadGame();

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
