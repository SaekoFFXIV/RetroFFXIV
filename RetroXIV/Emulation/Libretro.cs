using System;
using System.Runtime.InteropServices;

namespace RetroXIV.Emulation;

// P/Invoke surface for the libretro API (libretro.h). A libretro "core" (here, bsnes) is a native
// DLL exposing a fixed set of C functions; the frontend (this plugin) loads it, hands it callbacks,
// and drives it one frame at a time via retro_run().
public static class Libretro
{
    public const uint ApiVersion = 1;

    // Pixel formats (retro_pixel_format).
    public const uint PixelFormat0RGB1555 = 0;
    public const uint PixelFormatXRGB8888 = 1;
    public const uint PixelFormatRGB565 = 2;

    // Environment commands (retro_environment) - only the subset this frontend handles.
    public const uint EnvSetRotation = 1;
    public const uint EnvGetCanDupe = 3;
    public const uint EnvSetInputDescriptors = 11;
    public const uint EnvSetMessage = 6;
    public const uint EnvShutdown = 7;
    public const uint EnvGetSystemDirectory = 9;
    public const uint EnvSetPixelFormat = 10;
    public const uint EnvSetHwRender = 14;
    public const uint EnvGetVariable = 15;
    public const uint EnvSetVariables = 16;
    public const uint EnvGetVariableUpdate = 17;
    public const uint EnvSetSupportNoGame = 18;
    public const uint EnvGetLogInterface = 27;
    public const uint EnvGetSaveDirectory = 31;
    public const uint EnvSetSystemAvInfo = 32;
    public const uint EnvSetControllerInfo = 35;
    public const uint EnvSetGeometry = 37;
    public const uint EnvGetVfsInterface = 45 | EnvExperimental;
    public const uint EnvGetCoreOptionsVersion = 52;
    public const uint EnvSetCoreOptionsDisplay = 55;
    public const uint EnvGetPreferredHwRender = 56;
    public const uint EnvSetDiskControlExtInterface = 58;
    public const uint EnvSetCoreOptionsUpdateDisplayCallback = 69;

    // Experimental-flagged commands carry RETRO_ENVIRONMENT_EXPERIMENTAL in the high bits.
    public const uint EnvExperimental = 0x10000;
    public const uint EnvGetHwRenderInterface = 41 | EnvExperimental;

    // Hardware context types (retro_hw_context_type) this frontend offers.
    public const int HwContextD3D11 = 7;

    // retro_hw_render_interface_d3d11 identity (libretro_d3d.h).
    public const int HwRenderInterfaceD3D11 = 3;
    public const uint HwRenderInterfaceD3D11Version = 1;

    // Input devices (retro_device).
    public const uint DeviceNone = 0;
    public const uint DeviceJoypad = 1;
    public const uint DeviceAnalog = 5;

    // Joypad button IDs (RETRO_DEVICE_ID_JOYPAD_*).
    public const uint JoypadB = 0;
    public const uint JoypadY = 1;
    public const uint JoypadSelect = 2;
    public const uint JoypadStart = 3;
    public const uint JoypadUp = 4;
    public const uint JoypadDown = 5;
    public const uint JoypadLeft = 6;
    public const uint JoypadRight = 7;
    public const uint JoypadA = 8;
    public const uint JoypadX = 9;
    public const uint JoypadL = 10;
    public const uint JoypadR = 11;
    public const uint JoypadL2 = 12;
    public const uint JoypadR2 = 13;
    public const uint JoypadL3 = 14;
    public const uint JoypadR3 = 15;

    // Analog stick indices.
    public const uint AnalogIndexLeft = 0;
    public const uint AnalogIndexRight = 1;
    public const uint AnalogIdX = 0;
    public const uint AnalogIdY = 1;

    // Memory types (retro_memory).
    public const uint MemorySaveRam = 0;
    public const uint MemoryRtc = 1;
    public const uint MemorySystemRam = 2;
    public const uint MemoryVideoRam = 3;

    // Log levels (retro_log_level).
    public const int LogLevelDebug = 0;
    public const int LogLevelInfo = 1;
    public const int LogLevelWarn = 2;
    public const int LogLevelError = 3;
}

// C99 bool is 1 byte; every bool below is marshaled as I1 to match, otherwise the core and the
// frontend disagree on struct layout and return values.

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemInfo
{
    public IntPtr LibraryName;
    public IntPtr LibraryVersion;
    public IntPtr ValidExtensions;
    [MarshalAs(UnmanagedType.I1)] public bool NeedFullpath;
    [MarshalAs(UnmanagedType.I1)] public bool BlockExtract;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroGameGeometry
{
    public uint BaseWidth;
    public uint BaseHeight;
    public uint MaxWidth;
    public uint MaxHeight;
    public float AspectRatio;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemTiming
{
    public double Fps;
    public double SampleRate;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemAvInfo
{
    public RetroGameGeometry Geometry;
    public RetroSystemTiming Timing;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroGameInfo
{
    public IntPtr Path;
    public IntPtr Data;
    public UIntPtr Size;
    public IntPtr Meta;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroVariable
{
    public IntPtr Key;
    public IntPtr Value;
}

// Callbacks the frontend implements and hands to the core.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
public delegate bool RetroEnvironmentDelegate(uint cmd, IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroVideoRefreshDelegate(IntPtr data, uint width, uint height, UIntPtr pitch);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroAudioSampleDelegate(short left, short right);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate UIntPtr RetroAudioSampleBatchDelegate(IntPtr data, UIntPtr frames);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroInputPollDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate short RetroInputStateDelegate(uint port, uint device, uint index, uint id);

// retro_log_printf_t. The variadic arguments arrive as a native va_list
// pointer and are formatted on the C runtime side (vsnprintf), so they stay
// opaque here.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroLogPrintfDelegate(int level, IntPtr format, IntPtr vaList);

[StructLayout(LayoutKind.Sequential)]
public struct RetroLogCallback
{
    public RetroLogPrintfDelegate Log;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroCoreOptionsDisplay
{
    public IntPtr Key;
    [MarshalAs(UnmanagedType.I1)] public bool Visible;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
public delegate bool RetroCoreOptionsUpdateDisplayDelegate();

[StructLayout(LayoutKind.Sequential)]
public struct RetroCoreOptionsUpdateDisplayCallback
{
    public RetroCoreOptionsUpdateDisplayDelegate Callback;
}

// retro_hw_render_callback, filled by the core in SET_HW_RENDER. Only the
// head of the struct is marshaled; the trailing fields are never read here.
[StructLayout(LayoutKind.Sequential)]
public struct RetroHwRenderCallback
{
    public int ContextType;
    public IntPtr ContextReset;
    public IntPtr GetCurrentFramebuffer;
    public IntPtr GetProcAddress;
    [MarshalAs(UnmanagedType.I1)] public bool Depth;
    [MarshalAs(UnmanagedType.I1)] public bool Stencil;
    [MarshalAs(UnmanagedType.I1)] public bool BottomLeftOrigin;
    public uint VersionMajor;
    public uint VersionMinor;
    [MarshalAs(UnmanagedType.I1)] public bool CacheContext;
    public IntPtr ContextDestroy;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroHwContextResetDelegate();

// retro_hw_render_interface_d3d11 (libretro_d3d.h), written by the frontend
// in GET_HW_RENDER_INTERFACE.
[StructLayout(LayoutKind.Sequential)]
public struct RetroHwRenderInterfaceD3D11
{
    public int InterfaceType;
    public uint InterfaceVersion;
    public IntPtr Handle;
    public IntPtr Device;
    public IntPtr Context;
    public int FeatureLevel;
    public IntPtr D3DCompile;
}

// Functions the core exports and the frontend calls. Resolved at runtime via NativeLibrary because
// the core DLL path is user-configurable.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate uint RetroApiVersionDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroInitDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroDeinitDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroGetSystemInfoDelegate(out RetroSystemInfo info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroGetSystemAvInfoDelegate(out RetroSystemAvInfo info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
public delegate bool RetroLoadGameDelegate(ref RetroGameInfo info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroUnloadGameDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroRunDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroResetDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate UIntPtr RetroSerializeSizeDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
public delegate bool RetroSerializeDelegate(IntPtr data, UIntPtr size);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
public delegate bool RetroUnserializeDelegate(IntPtr data, UIntPtr size);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate IntPtr RetroGetMemoryDataDelegate(uint id);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate UIntPtr RetroGetMemorySizeDelegate(uint id);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroSetEnvironmentDelegate(RetroEnvironmentDelegate cb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroSetVideoRefreshDelegate(RetroVideoRefreshDelegate cb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroSetAudioSampleDelegate(RetroAudioSampleDelegate cb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroSetAudioSampleBatchDelegate(RetroAudioSampleBatchDelegate cb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroSetInputPollDelegate(RetroInputPollDelegate cb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RetroSetInputStateDelegate(RetroInputStateDelegate cb);
