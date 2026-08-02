using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EmulatorStream;

// P/Invoke bindings for snes_h264.dll (the thin C++ wrapper around OpenH264).
// The wrapper loads openh264-2.6.0-win64.dll at runtime via LoadLibrary, so
// both DLLs just need to sit next to the plugin DLL.
internal static class H264Native
{
    private const string Lib = "snes_h264";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr snes_encoder_create(int width, int height, float fps, int bitrate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int snes_encoder_encode(
        IntPtr handle, byte* rgba, byte** outBuf, int* outLen, int* frameType);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void snes_encoder_force_keyframe(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void snes_encoder_destroy(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr snes_decoder_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int snes_decoder_decode(
        IntPtr handle, byte* h264, int h264Len, byte** rgbaOut, int* width, int* height);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void snes_decoder_destroy(IntPtr handle);
}

// H.264 encoder wrapping the native snes_h264.dll.  Input: RGBA32 frames.
// Output: H.264 access units (Annex B byte stream with start codes).
// Not thread-safe — call from one thread only.
internal sealed class H264Encoder : IDisposable
{
    private IntPtr handle;

    public int Width { get; }
    public int Height { get; }

    public H264Encoder(int width, int height, float fps, int bitrate)
    {
        Width = width;
        Height = height;
        handle = H264Native.snes_encoder_create(width, height, fps, bitrate);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create H.264 encoder (is openh264-2.6.0-win64.dll present?)");
    }

    // Encode one RGBA32 frame.  Returns the H.264 bytes, or null if the
    // encoder skipped the frame (rate control).  isKeyFrame is set to true
    // for IDR frames.
    public unsafe byte[]? Encode(ReadOnlySpan<byte> rgba, out bool isKeyFrame)
    {
        isKeyFrame = false;
        if (handle == IntPtr.Zero)
            return null;

        byte* outBuf;
        int outLen;
        int frameType;

        fixed (byte* rgbaPtr = rgba)
        {
            var result = H264Native.snes_encoder_encode(handle, rgbaPtr, &outBuf, &outLen, &frameType);
            if (result != 0)
                return null;
        }

        if (outLen <= 0 || outBuf == null)
            return null;

        // frameType: 1 = IDR, 2 = I, 3 = P
        isKeyFrame = frameType is 1 or 2;

        var data = new byte[outLen];
        Marshal.Copy((IntPtr)outBuf, data, 0, outLen);
        return data;
    }

    public void ForceKeyFrame()
    {
        if (handle != IntPtr.Zero)
            H264Native.snes_encoder_force_keyframe(handle);
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            H264Native.snes_encoder_destroy(handle);
            handle = IntPtr.Zero;
        }
    }
}

// H.264 decoder wrapping the native snes_h264.dll.  Input: H.264 access
// units.  Output: RGBA32 frames.  Not thread-safe.
internal sealed class H264Decoder : IDisposable
{
    private IntPtr handle;

    public H264Decoder()
    {
        handle = H264Native.snes_decoder_create();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create H.264 decoder (is openh264-2.6.0-win64.dll present?)");
    }

    // Decode one H.264 access unit.  Returns RGBA32 bytes and sets
    // width/height, or returns null if no complete frame is available yet.
    public unsafe byte[]? Decode(ReadOnlySpan<byte> h264, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (handle == IntPtr.Zero)
            return null;

        byte* rgbaOut;
        int w, h;

        fixed (byte* h264Ptr = h264)
        {
            var result = H264Native.snes_decoder_decode(handle, h264Ptr, h264.Length, &rgbaOut, &w, &h);
            if (result != 1)
                return null;
        }

        width = w;
        height = h;

        var rgba = new byte[w * h * 4];
        Marshal.Copy((IntPtr)rgbaOut, rgba, 0, rgba.Length);
        return rgba;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            H264Native.snes_decoder_destroy(handle);
            handle = IntPtr.Zero;
        }
    }
}
