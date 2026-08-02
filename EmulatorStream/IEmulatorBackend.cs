using System;

namespace EmulatorStream;

/// <summary>
/// Contract that any emulator plugin implements so the shared streaming
/// library can observe its video and audio output without knowing the
/// emulation core's internals.
/// </summary>
public interface IEmulatorBackend
{
    /// <summary>True when a ROM/game is loaded and producing frames.</summary>
    bool IsGameLoaded { get; }

    /// <summary>Monotonically increasing counter; changes when a new frame is available.</summary>
    long FrameVersion { get; }

    /// <summary>Native resolution of the current game (before any upscale).</summary>
    int BaseWidth { get; }
    int BaseHeight { get; }

    /// <summary>Audio sample rate in Hz (e.g. 32000).</summary>
    double SampleRate { get; }

    /// <summary>
    /// Try to get the latest RGBA32 framebuffer.
    /// Returns false if no new frame is available since the last call.
    /// </summary>
    bool TryGetFrame(out byte[] rgba, out int width, out int height);

    /// <summary>
    /// Read interleaved stereo PCM audio samples.
    /// Returns the number of stereo frames actually written.
    /// </summary>
    int ReadStreamAudio(short[] destination, int maxFrames);
}
