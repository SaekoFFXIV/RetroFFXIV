using System;
using System.IO;
using SnesEmulator.Emulation;

// Usage: CoreSmoke -core <dll> -system <dir> -save <dir> -rom <file> [-frames N]
var corePath = string.Empty;
var systemDir = string.Empty;
var saveDir = string.Empty;
var romPath = string.Empty;
var frames = 300;

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "-core": corePath = args[++i]; break;
        case "-system": systemDir = args[++i]; break;
        case "-save": saveDir = args[++i]; break;
        case "-rom": romPath = args[++i]; break;
        case "-frames": frames = int.Parse(args[++i]); break;
    }
}

if (corePath.Length == 0 || romPath.Length == 0)
{
    Console.Error.WriteLine("usage: CoreSmoke -core <dll> -system <dir> -save <dir> -rom <file> [-frames N]");
    return 2;
}

Directory.CreateDirectory(saveDir);
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
};

core.Load(corePath);
Console.WriteLine($"[smoke] loaded: {core.LibraryName} {core.LibraryVersion}");

if (!core.LoadGame(romPath))
{
    Console.WriteLine("[smoke] FAIL: core refused to load the content");
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

var state = core.SaveState();
Console.WriteLine($"[smoke] save state: {(state != null ? $"{state.Length} bytes" : "unavailable")}");

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
