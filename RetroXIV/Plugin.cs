using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using EmulatorStream;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RetroXIV;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/retro";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    internal Configuration Configuration { get; private set; } = null!;

    private readonly EmulatorService emulator;
    private bool showWindow;
    private bool windowWasOpen;

    public Plugin()
    {
        // Add the plugin directory to the DLL search path so native DLLs
        // (snes_h264.dll, openh264-*.dll) can be found by P/Invoke.
        var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? "";
        if (!string.IsNullOrEmpty(pluginDir))
            SetDllDirectory(pluginDir);

        MigrateLegacyConfig();

        Configuration = (PluginInterface.GetPluginConfig() as Configuration) ?? new Configuration();

        var input = new InputManager(Configuration, KeyState);
        emulator = new EmulatorService(Configuration, PluginInterface, TextureProvider, Log, input, Framework, GameGui, ObjectTable, GameInteropProvider);
        emulator.CloseRequested += () => showWindow = false;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the retro emulator.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
    }

    // The plugin was renamed from SnesEmulator; carry the old config directory
    // (saves, BIOS layout) and the settings json over under the new name. Runs
    // on every launch but only copies files that are still missing, so a
    // partially failed migration resumes instead of staying stuck.
    private static void MigrateLegacyConfig()
    {
        try
        {
            var newDir = PluginInterface.ConfigDirectory;
            var parentDir = newDir.Parent!.FullName;
            var oldDir = Path.Combine(parentDir, "SnesEmulator");
            var migrated = false;

            if (Directory.Exists(oldDir))
            {
                foreach (var file in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(oldDir, file);
                    var destination = Path.Combine(newDir.FullName, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!File.Exists(destination))
                    {
                        File.Copy(file, destination);
                        migrated = true;
                    }
                }
            }

            // Dalamud stores the settings json beside the data directory
            // (pluginConfigs\<name>.json), not inside it.
            var oldConfig = Path.Combine(parentDir, "SnesEmulator.json");
            var newConfig = Path.Combine(parentDir, "RetroXIV.json");
            if (File.Exists(oldConfig) && !File.Exists(newConfig))
            {
                File.Move(oldConfig, newConfig);
                migrated = true;
            }

            if (migrated)
            {
                Log.Information("Migrated legacy SnesEmulator config to {Dir}", newDir.FullName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Legacy config migration failed");
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        CommandManager.RemoveHandler(CommandName);
        emulator.Dispose();
    }

    private void OpenWindow() => showWindow = true;

    private void OnCommand(string command, string arguments) => showWindow = !showWindow;

    private void Draw()
    {
        if (!showWindow)
        {
            if (windowWasOpen)
            {
                windowWasOpen = false;
                emulator.OnMainWindowClosed();
            }

            emulator.SetFocused(false);
            emulator.SetWindowOpen(false);
            // World screens live independently of the control deck.
            emulator.DrawWorldScreen();
            return;
        }

        windowWasOpen = true;

        emulator.SetWindowOpen(true);
        emulator.DrawMainWindow(ref showWindow);
        emulator.DrawViewerWindow();
        emulator.DrawWorldScreen();
    }
}
