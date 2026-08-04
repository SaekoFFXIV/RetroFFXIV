using System;
using System.Runtime.InteropServices;

namespace EmulatorStream;

// P/Invoke bindings for snes_opus.dll (the thin C wrapper around libopus).
// The wrapper loads opus.dll at runtime via LoadLibrary, so both DLLs just
// need to sit next to the plugin DLL.
internal static class OpusNative
{
    private const string Lib = "snes_opus";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr snes_opus_encoder_create(int sampleRate, int channels, int bitrate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int snes_opus_encode(
        IntPtr handle, short* pcm, int frameSamples, byte** outBuf, int* outLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void snes_opus_encoder_destroy(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr snes_opus_decoder_create(int sampleRate, int channels);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int snes_opus_decode(
        IntPtr handle, byte* data, int len, short* pcmOut, int maxFrameSamples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void snes_opus_decoder_destroy(IntPtr handle);
}

// Opus encoder wrapping the native snes_opus.dll.  Input: interleaved int16
// stereo PCM at 48 kHz.  Output: one Opus packet per Encode call.
// Not thread-safe — call from one thread only.
internal sealed class OpusEncoder : IDisposable
{
    private IntPtr handle;

    public int SampleRate { get; }
    public int Channels { get; }

    public OpusEncoder(int sampleRate, int channels, int bitrate)
    {
        SampleRate = sampleRate;
        Channels = channels;
        handle = OpusNative.snes_opus_encoder_create(sampleRate, channels, bitrate);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Opus encoder (is opus.dll present?)");
    }

    // Encode one frame of interleaved int16 PCM.  frameSamples is per
    // channel and must be a valid Opus frame size for the sample rate
    // (e.g. 960 = 20 ms at 48 kHz).  Returns the Opus packet bytes.
    public unsafe byte[] Encode(ReadOnlySpan<short> pcm, int frameSamples)
    {
        if (handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(OpusEncoder));
        if (pcm.Length < frameSamples * Channels)
            throw new ArgumentException("PCM buffer too small for frame");

        byte* outBuf;
        int outLen;

        fixed (short* pcmPtr = pcm)
        {
            var result = OpusNative.snes_opus_encode(handle, pcmPtr, frameSamples, &outBuf, &outLen);
            if (result != 0)
                throw new InvalidOperationException("Opus encode failed");
        }

        var data = new byte[outLen];
        Marshal.Copy((IntPtr)outBuf, data, 0, outLen);
        return data;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            OpusNative.snes_opus_encoder_destroy(handle);
            handle = IntPtr.Zero;
        }
    }
}

// Opus decoder wrapping the native snes_opus.dll.  Input: one Opus packet.
// Output: interleaved int16 PCM at the decoder's sample rate.
// Not thread-safe — call from one thread only.
internal sealed class OpusDecoder : IDisposable
{
    // Largest possible decoded frame: 120 ms.
    private const int MaxFrameSamples = 5760;

    private IntPtr handle;

    public int Channels { get; }

    public OpusDecoder(int sampleRate, int channels)
    {
        Channels = channels;
        handle = OpusNative.snes_opus_decoder_create(sampleRate, channels);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Opus decoder (is opus.dll present?)");
    }

    // Decode one Opus packet.  Returns interleaved int16 PCM bytes, or null
    // if the packet is corrupt.
    public unsafe byte[]? Decode(ReadOnlySpan<byte> packet)
    {
        if (handle == IntPtr.Zero)
            return null;

        var pcm = new short[MaxFrameSamples * Channels];
        int frames;

        fixed (byte* dataPtr = packet)
        fixed (short* pcmPtr = pcm)
        {
            frames = OpusNative.snes_opus_decode(handle, dataPtr, packet.Length, pcmPtr, MaxFrameSamples);
        }

        if (frames <= 0)
            return null;

        var bytes = new byte[frames * Channels * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            OpusNative.snes_opus_decoder_destroy(handle);
            handle = IntPtr.Zero;
        }
    }
}
