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

namespace SnesEmulator;

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

    internal Configuration Configuration { get; private set; } = null!;

    private readonly EmulatorService emulator;
    private bool showWindow;

    public Plugin()
    {
        // Add the plugin directory to the DLL search path so native DLLs
        // (snes_h264.dll, openh264-*.dll) can be found by P/Invoke.
        var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? "";
        if (!string.IsNullOrEmpty(pluginDir))
            SetDllDirectory(pluginDir);

        Configuration = (PluginInterface.GetPluginConfig() as Configuration) ?? new Configuration();

        var input = new InputManager(Configuration, KeyState);
        emulator = new EmulatorService(Configuration, PluginInterface, TextureProvider, Log, input, Framework, GameGui, ObjectTable);
        emulator.CloseRequested += () => showWindow = false;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the retro emulator.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
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
            emulator.SetFocused(false);
            emulator.SetWindowOpen(false);
            // World screens live independently of the control deck.
            emulator.DrawWorldScreen();
            return;
        }

        emulator.SetWindowOpen(true);
        emulator.DrawMainWindow(ref showWindow);
        emulator.DrawViewerWindow();
        emulator.DrawWorldScreen();
    }
}
