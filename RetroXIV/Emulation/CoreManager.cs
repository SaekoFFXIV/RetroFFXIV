using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RetroXIV.Emulation;

public sealed record CoreInfo(string Path, string Name, string Version, string[] Extensions, bool NeedFullpath)
{
    // Keep libretro's real identity for loading/configuration, but present
    // straightforward platform names in the player-facing selector.
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path) switch
    {
        var file when file.Contains("bsnes", StringComparison.OrdinalIgnoreCase) => "SNES",
        var file when file.Contains("blastem", StringComparison.OrdinalIgnoreCase) => "Sega",
        var file when file.Contains("mgba", StringComparison.OrdinalIgnoreCase) => "GBA",
        var file when file.Contains("gambatte", StringComparison.OrdinalIgnoreCase) => "GB/GBC",
        var file when file.Contains("mednafen_psx", StringComparison.OrdinalIgnoreCase) => "PS1",
        var file when file.Contains("beetle_psx", StringComparison.OrdinalIgnoreCase) => "PS1",
        var file when file.Contains("pcsx2", StringComparison.OrdinalIgnoreCase) => "PS2",
        var file when file.Contains("lrps2", StringComparison.OrdinalIgnoreCase) => "PS2",
        _ => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}",
    };

    // PS2 frames are heavy to upscale; the frontend caps the display scale for these cores.
    public bool IsPs2 => DisplayName == "PS2";
}

// Discovers libretro core DLLs by scanning the plugin's cores/ subdirectory (and the plugin
// directory itself for backward compatibility), then queries each for its system info
// (retro_get_system_info) without fully initializing it. The result is a cached list of
// available cores with their supported ROM extensions, driving the core selector UI and
// ROM browser filtering.
public sealed class CoreManager
{
    private readonly string pluginDir;
    private readonly Action<string, object[]> logInfo;
    private readonly Action<string, object[]> logError;

    public List<CoreInfo> Cores { get; } = new();
    public string ScanError { get; private set; } = string.Empty;

    public CoreManager(string pluginDir, Action<string, object[]> logInfo, Action<string, object[]> logError)
    {
        this.pluginDir = pluginDir;
        this.logInfo = logInfo;
        this.logError = logError;
        Scan();
    }

    public void Scan()
    {
        Cores.Clear();
        ScanError = string.Empty;

        var paths = new List<string>();

        // Primary: cores/ subdirectory next to the plugin DLL.
        var coresDir = Path.Combine(pluginDir, "cores");
        if (Directory.Exists(coresDir))
        {
            paths.AddRange(Directory.GetFiles(coresDir, "*_libretro.dll"));
        }

        // Fallback: the plugin directory itself (backward compat with bundled bsnes).
        foreach (var dll in Directory.GetFiles(pluginDir, "*_libretro.dll"))
        {
            if (!paths.Any(p => string.Equals(Path.GetFileName(p), Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(dll);
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                var info = QueryCore(path);
                if (info != null)
                {
                    Cores.Add(info);
                    logInfo("Discovered core: {Name} {Version} ({Exts})",
                        [info.Name, info.Version, string.Join(", ", info.Extensions)]);
                }
            }
            catch (Exception ex)
            {
                logError("Failed to query core {Path}: {Error}", [path, ex.Message]);
            }
        }

        if (Cores.Count == 0)
        {
            ScanError = "No libretro cores found. Drop *_libretro.dll files into the cores/ folder.";
        }
    }

    public CoreInfo? FindByPath(string path) =>
        Cores.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

    public CoreInfo? FindByName(string name) =>
        Cores.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // Returns the default core: the first discovered core, preferring one named "bsnes" for
    // backward compatibility with the existing SNES-first setup.
    public CoreInfo? GetDefault() =>
        Cores.FirstOrDefault(c => c.Name.Contains("bsnes", StringComparison.OrdinalIgnoreCase)) ?? Cores.FirstOrDefault();

    private static CoreInfo? QueryCore(string dllPath)
    {
        IntPtr library = IntPtr.Zero;
        try
        {
            library = NativeLibrary.Load(dllPath);

            if (!NativeLibrary.TryGetExport(library, "retro_get_system_info", out var export))
            {
                return null;
            }

            var getSystemInfo = Marshal.GetDelegateForFunctionPointer<RetroGetSystemInfoDelegate>(export);
            getSystemInfo(out var info);

            var name = Marshal.PtrToStringAnsi(info.LibraryName) ?? Path.GetFileNameWithoutExtension(dllPath);
            var version = Marshal.PtrToStringAnsi(info.LibraryVersion) ?? string.Empty;
            var extRaw = Marshal.PtrToStringAnsi(info.ValidExtensions) ?? string.Empty;

            var extensions = extRaw
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : $".{e.ToLowerInvariant()}")
                .ToArray();

            // need_fullpath cores (disc images) must be loaded by path, never
            // copied into managed memory by the frontend.
            return new CoreInfo(dllPath, name, version, extensions, info.NeedFullpath);
        }
        finally
        {
            if (library != IntPtr.Zero)
            {
                NativeLibrary.Free(library);
            }
        }
    }
}
