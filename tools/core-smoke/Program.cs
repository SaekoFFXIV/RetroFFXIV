using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SnesEmulator.Emulation;
using SnesEmulator.Rendering;

// Usage: CoreSmoke -core <dll> -system <dir> -save <dir> -rom <file> [-frames N]
//        CoreSmoke -core <dll> -system <dir> -info   (no content: dump identity + options)
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

var corePath = string.Empty;
var systemDir = string.Empty;
var saveDir = string.Empty;
var romPath = string.Empty;
var frames = 300;
var infoOnly = Array.IndexOf(args, "-info") >= 0;
var noGame = Array.IndexOf(args, "-nogame") >= 0;
var skipState = Array.IndexOf(args, "-nosave") >= 0;
var delayAfterLoadMs = 0;
var optionSets = new List<(string Key, string Value)>();

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "-core": corePath = args[++i]; break;
        case "-system": systemDir = args[++i]; break;
        case "-save": saveDir = args[++i]; break;
        case "-rom": romPath = args[++i]; break;
        case "-frames": frames = int.Parse(args[++i]); break;
        case "-delay-ms": delayAfterLoadMs = int.Parse(args[++i]); break;
        case "-set":
        {
            var kv = args[++i].Split('=', 2);
            if (kv.Length == 2)
            {
                optionSets.Add((kv[0], kv[1]));
            }

            break;
        }
    }
}

CrashProbe.Install();

if (corePath.Length == 0 || (!infoOnly && !noGame && romPath.Length == 0))
{
    Console.Error.WriteLine("usage: CoreSmoke -core <dll> -system <dir> -save <dir> -rom <file> [-frames N]");
    Console.Error.WriteLine("       CoreSmoke -core <dll> -system <dir> -info");
    Console.Error.WriteLine("       CoreSmoke -core <dll> -system <dir> -save <dir> -nogame [-frames N]");
    return 2;
}

if (saveDir.Length > 0)
{
    Directory.CreateDirectory(saveDir);
}

Console.WriteLine($"[smoke] core    = {corePath}");
Console.WriteLine($"[smoke] system  = {systemDir}");
Console.WriteLine($"[smoke] save    = {saveDir}");
Console.WriteLine($"[smoke] content = {romPath}");

var failed = false;
using var core = new RetroCore
{
    SystemDirectory = systemDir,
    SaveDirectory = saveDir,
    InputState = (_, _, _, _) => 0,
    BackgroundError = ex =>
    {
        Console.WriteLine($"[smoke] BACKGROUND ERROR: {ex}");
        failed = true;
    },
    LogReceived = (level, text) => Console.WriteLine($"[core:{level}] {text}"),
    EnvironmentTrace = Array.IndexOf(args, "-trace") >= 0
        ? (cmd, handled) => Console.WriteLine($"[env] {cmd} {(handled ? "ok" : "NO")}")
        : null,
};

foreach (var (key, value) in optionSets)
{
    core.OverrideVariable(key, value);
}

// -nohw: stay a pure software-frame frontend (no D3D11 context offered);
// hardware-rendering cores are refused SET_HW_RENDER and must fall back
// to their CPU renderer.
if (Array.IndexOf(args, "-nohw") < 0)
{
    core.HwRender = D3D11HwContext.TryCreate();
}
else if (optionSets.All(o => !string.Equals(o.Key, "pcsx2_renderer", StringComparison.OrdinalIgnoreCase)))
{
    core.OverrideVariable("pcsx2_renderer", "Software (SW)");
}

core.Load(corePath);
Console.WriteLine($"[smoke] loaded: {core.LibraryName} {core.LibraryVersion}");

if (delayAfterLoadMs > 0)
{
    System.Threading.Thread.Sleep(delayAfterLoadMs);
}

if (Array.IndexOf(args, "-inspect") >= 0)
{
    Inspector.Dump(corePath);
}

if (infoOnly)
{
    Console.WriteLine($"[smoke] extensions: {string.Join(", ", core.Extensions)}");
    Console.WriteLine($"[smoke] options ({core.VariableDeclarations.Count}):");
    foreach (var (key, raw) in core.VariableDeclarations)
    {
        Console.WriteLine($"[smoke]   {key} -> [{core.GetVariableSelection(key)}] from: {raw}");
    }

    return 0;
}

var loaded = noGame ? core.LoadNoGame() : core.LoadGame(romPath);
if (!loaded)
{
    Console.WriteLine($"[smoke] FAIL: {core.LastLoadError ?? (noGame
        ? "core refused to boot without content"
        : "core refused to load the content")}");
    return 1;
}

Console.WriteLine($"[smoke] av info: {core.BaseWidth}x{core.BaseHeight} @ {core.Fps:0.###} fps, "
                  + $"{core.SampleRate:0.#} Hz, aspect={core.AspectRatio:0.####}");

for (var i = 0; i < frames; i++)
{
    core.RunFrame();
    if (failed)
    {
        break;
    }
}

var gotFrame = core.TryGetFrame(out var rgba, out var w, out var h);
Console.WriteLine($"[smoke] after {frames} frames: video={(gotFrame ? $"{w}x{h} ({rgba.Length} bytes)" : "NONE")}, "
                  + $"frameVersion={core.FrameVersion}, bufferedAudio={core.BufferedAudioFrames} frames");

var state = skipState ? null : core.SaveState();
Console.WriteLine($"[smoke] save state: {(skipState ? "skipped" : state != null ? $"{state.Length} bytes" : "unavailable")}");

// Optional BMP dump of the latest frame for visual inspection.
var outIndex = Array.IndexOf(args, "-out");
if (outIndex >= 0 && outIndex + 1 < args.Length && gotFrame)
{
    WriteBmp(args[outIndex + 1], rgba, w, h);
    Console.WriteLine($"[smoke] frame dumped: {args[outIndex + 1]}");
}

core.UnloadGame();

foreach (var file in Directory.GetFiles(saveDir))
{
    Console.WriteLine($"[smoke] save dir file: {Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)");
}

if (!gotFrame || core.FrameVersion == 0)
{
    Console.WriteLine("[smoke] FAIL: no video frames produced");
    return 1;
}

Console.WriteLine("[smoke] PASS");
return failed ? 1 : 0;

static void WriteBmp(string path, byte[] rgba, int width, int height)
{
    var rowBytes = width * 3;
    var paddedRow = (rowBytes + 3) & ~3;
    var pixelDataSize = paddedRow * height;
    var fileSize = 54 + pixelDataSize;

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0x4D42);
    writer.Write(fileSize);
    writer.Write(0);
    writer.Write(54);
    writer.Write(40);
    writer.Write(width);
    writer.Write(height);
    writer.Write((ushort)1);
    writer.Write((ushort)24);
    writer.Write(0);
    writer.Write(pixelDataSize);
    writer.Write(2835);
    writer.Write(2835);
    writer.Write(0);
    writer.Write(0);

    var padding = new byte[paddedRow - rowBytes];
    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var o = (y * width + x) * 4;
            writer.Write(rgba[o + 2]);
            writer.Write(rgba[o + 1]);
            writer.Write(rgba[o]);
        }

        writer.Write(padding);
    }
}

// Prints the faulting module+offset when a native core crashes the process,
// since no debugger is installed on this box.
internal static class CrashProbe
{
    private delegate long VectoredHandler(IntPtr exceptionPointers);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort RtlCaptureStackBackTrace(uint skip, uint count,
        IntPtr[] buffer, IntPtr hash);

    private static readonly VectoredHandler keepAlive = OnException;

    public static void Install() => AddVectoredExceptionHandler(1, keepAlive);

    private static string Describe(long address)
    {
        var module = System.Diagnostics.Process.GetCurrentProcess().Modules
            .Cast<System.Diagnostics.ProcessModule>()
            .FirstOrDefault(m => address >= m.BaseAddress.ToInt64() &&
                                 address < m.BaseAddress.ToInt64() + m.ModuleMemorySize);
        return module != null
            ? $"{module.ModuleName}+0x{address - module.BaseAddress.ToInt64():x}"
            : $"0x{address:x}";
    }

    private static long OnException(IntPtr pointers)
    {
        try
        {
            var record = System.Runtime.InteropServices.Marshal.ReadIntPtr(pointers);
            var code = System.Runtime.InteropServices.Marshal.ReadInt32(record, 0);
            if (code != unchecked((int)0xC0000005))
            {
                return 0;
            }

            var rip = System.Runtime.InteropServices.Marshal.ReadInt64(record, 0x10);
            var kind = System.Runtime.InteropServices.Marshal.ReadInt64(record, 0x20);
            var target = System.Runtime.InteropServices.Marshal.ReadInt64(record, 0x28);
            var verb = kind == 0 ? "read" : kind == 1 ? "write" : kind == 8 ? "exec" : "?";
            Console.Error.WriteLine($"[smoke] CRASH: AV {verb} 0x{target:x} at {Describe(rip)}");

            var frames = new IntPtr[16];
            var count = RtlCaptureStackBackTrace(0, 16, frames, IntPtr.Zero);
            for (var i = 0; i < count; i++)
            {
                Console.Error.WriteLine($"[smoke]   #{i} {Describe(frames[i].ToInt64())}");
            }

            Console.Error.WriteLine("[smoke] managed stack:");
            Console.Error.WriteLine(System.Environment.StackTrace);
            Inspector.DumpAll();
        }
        catch
        {
            // The crash reporter must never throw.
        }

        return 0; // EXCEPTION_CONTINUE_SEARCH
    }
}

// Dumps LRPS2's presentation control block (RVAs for the v2.0.0-093f66b
// build) to identify which resource lookup the core is stuck on.
internal static class Inspector
{
    private static IntPtr baseAddress;

    // (rva, label) pairs for the GS presenter state block.
    private static readonly (long Rva, string Label)[] slots =
    {
        (0x3228D18, "CTX_PTR"),
        (0x3228D20, "STATE"),
        (0x3228D24, "FLAG"),
        (0x3228D25, "BYTE25"),
        (0x3228D26, "BYTE26"),
        (0x2338408, "TABLE"),
        (0x3215E90, "SINGLETON"),
        (0x9C9914, "MODE_BYTE"),
    };

    public static void Dump(string corePath)
    {
        var name = System.IO.Path.GetFileName(corePath);
        foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            if (string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase))
            {
                baseAddress = m.BaseAddress;
                break;
            }
        }

        if (baseAddress == IntPtr.Zero)
        {
            Console.WriteLine("[inspect] core module not found");
            return;
        }

        DumpAll();
    }

    public static void DumpAll()
    {
        var b = baseAddress != IntPtr.Zero
            ? baseAddress
            : FindModule();
        if (b == IntPtr.Zero)
        {
            return;
        }

        foreach (var (rva, label) in slots)
        {
            var p = b + checked((int)rva);
            try
            {
                if (label is "STATE")
                {
                    Console.Error.WriteLine($"[inspect] {label} = {System.Runtime.InteropServices.Marshal.ReadInt32(p)}");
                }
                else if (label is "FLAG" or "BYTE25" or "BYTE26" or "MODE_BYTE")
                {
                    Console.Error.WriteLine($"[inspect] {label} = {System.Runtime.InteropServices.Marshal.ReadByte(p)}");
                }
                else
                {
                    Console.Error.WriteLine($"[inspect] {label} = 0x{System.Runtime.InteropServices.Marshal.ReadIntPtr(p):x}");
                }
            }
            catch
            {
                Console.Error.WriteLine($"[inspect] {label} = <unreadable>");
            }
        }

        // MSVC x64 std::string entries: { buf/ptr[16], size@0x10, cap@0x18 }.
        for (var i = 0; i < 8; i++)
        {
            try
            {
                var e = b + 0x288E60 + i * 32;
                var size = System.Runtime.InteropServices.Marshal.ReadIntPtr(e + 0x10).ToInt64();
                var cap = System.Runtime.InteropServices.Marshal.ReadIntPtr(e + 0x18).ToInt64();
                string s;
                if (cap < 16)
                {
                    s = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(e, (int)Math.Min(size, 16)) ?? string.Empty;
                }
                else
                {
                    var ptr = System.Runtime.InteropServices.Marshal.ReadIntPtr(e);
                    s = ptr == IntPtr.Zero ? "<null>" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
                }

                Console.Error.WriteLine($"[inspect] entry[{i}] size={size} cap={cap} \"{s}\"");
            }
            catch
            {
                Console.Error.WriteLine($"[inspect] entry[{i}] <unreadable>");
            }
        }

        // Singleton object head: [obj+0] = destroy callback, [obj+8] = lookup fn.
        try
        {
            var obj = System.Runtime.InteropServices.Marshal.ReadIntPtr(b + 0x3215E90);
            if (obj != IntPtr.Zero)
            {
                var m0 = System.Runtime.InteropServices.Marshal.ReadIntPtr(obj);
                var m8 = System.Runtime.InteropServices.Marshal.ReadIntPtr(obj + 8);
                Console.Error.WriteLine($"[inspect] singleton obj=0x{obj:x} [0]=0x{m0:x} (+0x{m0.ToInt64() - b.ToInt64():x}) [8]=0x{m8:x} (+0x{m8.ToInt64() - b.ToInt64():x})");
            }
        }
        catch
        {
        }
    }

    private static IntPtr FindModule()
    {
        foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            if (m.ModuleName.Contains("pcsx2", StringComparison.OrdinalIgnoreCase))
            {
                baseAddress = m.BaseAddress;
                return m.BaseAddress;
            }
        }

        return IntPtr.Zero;
    }
}
