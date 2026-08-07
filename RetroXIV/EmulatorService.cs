using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using RetroXIV.Emulation;
using RetroXIV.Rendering;
using RetroXIV.Streaming;
using EmulatorStream;
using FfxivCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace RetroXIV;

// Owns the emulation core and its presentation: a single window styled as one piano-black retro TV
// unit - a CRT screen with a control strip, plus a recessed control deck (ROM / Keyboard /
// Controller / Settings tabs) docked to the right. The deck collapses away via a button in the TV's
// bottom-right corner, leaving just the TV. The core runs on its own thread (see RetroCore); this
// class only displays the latest frame and handles input suppression on the game thread.
public sealed class EmulatorService : IDisposable
{
    private const int MaxScale = 5;

    // PS2 cores cap at a lower display scale; higher options stay visible but disabled.
    private const int Ps2MaxScale = 2;

    private const float Bezel = 26f;
    private const float ControlStrip = 48f;
    private const float PanelGap = 10f;
    private const float PanelWidth = 400f;
    private const float DeckContentInsetX = 8f;
    private const float DeckContentInsetTop = 10f;
    private const float DeckContentInsetBottom = 18f;
    private const float TextContentInset = 12f;
    private const int PanelThemeColorCount = 26;
    private const int PanelThemeStyleVarCount = 6;

    private const uint Black = 0xFF000000;

    // Restrained graphite palette: one cool accent, clear hierarchy, and
    // enough opacity that gameplay never competes with the controls.
    private const uint ShellBody = 0xF01B1D20;
    private const uint ShellHighlight = 0xFF30343B;
    private const uint Sheen = 0x302A2E32;
    private const uint GlossEdge = 0xFF3A414A;
    private const uint GlossFill = 0xFF13161A;
    private const uint NeonCyan = 0xFFEBC65D;         // #5DC6EB — primary accent
    private const uint NeonPink = 0xFF6060D0;         // #D06060 — destructive accent
    private const uint NeonAmber = 0xFFE0B46E;        // #6EB4E0 — secondary accent
    private const uint NeonViolet = 0xFFC0B4A8;       // #A8B4C0 — neutral indicator
    private const uint DeckBody = 0xFF1A1C20;
    private const uint TextDim = 0xFF9AA3AC;
    private const uint TextBright = 0xFFE8EDF2;
    private const uint LedOn = 0xFFEBC65D;
    private const uint LedOff = 0xFF6060D0;
    private const float BindingColumnX = 126f;

    private static readonly SnesButton[] ButtonOrder =
    {
        SnesButton.Up, SnesButton.Down, SnesButton.Left, SnesButton.Right,
        SnesButton.A, SnesButton.B, SnesButton.X, SnesButton.Y,
        SnesButton.L, SnesButton.R, SnesButton.L2, SnesButton.R2,
        SnesButton.L3, SnesButton.R3,
        SnesButton.Start, SnesButton.Select,
    };

    private static readonly SnesButton[] ControllerButtonOrder =
    {
        SnesButton.A, SnesButton.B, SnesButton.X, SnesButton.Y,
        SnesButton.L, SnesButton.R, SnesButton.L2, SnesButton.R2,
        SnesButton.L3, SnesButton.R3,
        SnesButton.Start, SnesButton.Select,
    };

    // Keyboard keys that drive the analog sticks on PS1/PS2.
    private static readonly SnesButton[] LeftStickOrder =
    {
        SnesButton.LeftStickUp, SnesButton.LeftStickDown,
        SnesButton.LeftStickLeft, SnesButton.LeftStickRight,
    };

    private static readonly SnesButton[] RightStickOrder =
    {
        SnesButton.RightStickUp, SnesButton.RightStickDown,
        SnesButton.RightStickLeft, SnesButton.RightStickRight,
    };

    private static readonly string[] DeckTabLabels = { "ROM", "Controls", "Settings", "Sync", "Friends" };

    private static readonly ushort[] XInputButtonFlags =
    {
        GamepadReader.A, GamepadReader.B, GamepadReader.X, GamepadReader.Y,
        GamepadReader.LeftShoulder, GamepadReader.RightShoulder,
        GamepadReader.LeftTrigger, GamepadReader.RightTrigger,
        GamepadReader.Start, GamepadReader.Back,
        GamepadReader.DPadUp, GamepadReader.DPadDown, GamepadReader.DPadLeft, GamepadReader.DPadRight,
        GamepadReader.LeftThumb, GamepadReader.RightThumb,
    };

    private readonly Configuration config;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;
    private readonly InputManager inputManager;
    private readonly RomBrowser romBrowser;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly CoreManager coreManager;
    private CoreInfo? selectedCore;

    // The effective scale cap for the current core. The saved config value is left untouched,
    // so switching back to a non-PS2 core restores the player's preferred scale.
    private int CurrentMaxScale => selectedCore is { IsPs2: true } ? Ps2MaxScale : MaxScale;

    // Save states are gated off where the core cannot round-trip them safely
    // (LRPS2 crashes the process on unserialize); see CoreInfo.SupportsSaveStates.
    private bool StatesSupported => selectedCore is not { SupportsSaveStates: false };
    private readonly XivAuthService xivAuth;
    private readonly StreamConfig streamConfig;
    private StreamPanel? streamPanel;
    private NetplayPanel? netplayPanel;
    private WorldScreenRenderer? worldScreen;

    // One world screen per watched stream, keyed by player ID.
    private readonly System.Collections.Generic.Dictionary<string, WorldScreenRenderer> watchScreens = new();
    private readonly System.Collections.Generic.Dictionary<string, long> watchVersions = new();
    private readonly System.Collections.Generic.Dictionary<string, long> watchScreenStateVersions = new();
    private WorldScreenState? lastPublishedLiveScreenState;

    // Live presence for synced friends (polled via sync_check).
    private readonly System.Collections.Generic.Dictionary<string, LivePlayerInfo> liveStatus = new();
    private DateTime lastSyncCheck = DateTime.MinValue;
    private bool syncCheckInFlight;
    private string? watchError;

    // Friends roster — the relay owns it (keyed by the XIVAuth identity), so
    // a config reset or a new machine cannot lose friendships. This is the
    // local working copy: seeded from the config cache, reconciled on sync.
    private readonly List<SyncFriend> syncFriends;
    private readonly object friendsLock = new();
    private readonly HashSet<string> offlineFriendRemovals = new();
    private bool friendsFetched;
    private bool friendsSyncInFlight;
    private DateTime lastFriendsSync = DateTime.MinValue;

    // Player ID registration with the relay (tied to the XIVAuth account).
    private bool playerRegistering;
    private string? playerRegError;
    private DateTime lastRegisterAttempt = DateTime.MinValue;
    private DateTime idCopiedUntil = DateTime.MinValue;

    // Relay presence (who is online).
    private RelayPresence? presence;

    // DX11 depth-integrated world screens.
    private readonly Rendering.DxWorldRenderer? dxScreen;
    private long dxLocalVersion = -1;
    private byte[]? dxLocalFrame;
    private int dxLocalW, dxLocalH;
    private bool loggedDxCameraMatrices;
    private DateTime lastDxProjectionComparison = DateTime.MinValue;
    private readonly System.Collections.Generic.Dictionary<string, long> dxWatchVersions = new();
    private readonly System.Collections.Generic.Dictionary<string, (byte[] Rgba, int W, int H)> dxWatchFrames = new();

    private RetroCore? core;
    private D3D11HwContext? hwRender;
    private AudioPlayer? audio;
    private IDalamudTextureWrap? texture;
    private int textureWidth;
    private int textureHeight;
    private long lastFrameVersion = -1;
    private bool coreLoadAttempted;
    private volatile bool focused;

    private bool panelOpen = true;
    private bool screenOn;
    private int selectedDeckTab;
    // Child windows retain their ImGui scroll state across layout reloads.
    // Reset a tab once when it becomes active so its first controls cannot
    // reopen clipped under the fixed tab bar.
    private int resetDeckContentScrollTab;

    // CRT power animation: 0 = off, 1 = fully on, in-between = animating.
    private float crtAnim;          // current animation progress [0..1]
    private float crtAnimTarget;    // 0 or 1 — where we're heading
    private const float CrtAnimSpeed = 3.5f; // full transition in ~0.3s

    private SnesButton? rebindingKey;
    private SnesButton? rebindingController;
    private ushort controllerRebindBaseline;

    private string romPath = string.Empty;
    private string status = "Loading core...";
    private string gameKey = string.Empty;
    private string saveStateStatus = string.Empty;

    public event Action? CloseRequested;

    public EmulatorService(Configuration config, IDalamudPluginInterface pluginInterface, ITextureProvider textureProvider, IPluginLog log, InputManager inputManager, IFramework framework, IGameGui gameGui, IObjectTable objectTable, IGameInteropProvider interop)
    {
        this.config = config;
        this.pluginInterface = pluginInterface;
        this.textureProvider = textureProvider;
        this.log = log;
        this.inputManager = inputManager;
        this.framework = framework;
        this.gameGui = gameGui;
        this.objectTable = objectTable;

        syncFriends = new List<SyncFriend>(config.SyncFriends);

        // Upgraded configs predate newer binding targets (stick keys, L3/R3);
        // backfill anything missing with defaults without touching binds the
        // player already made.
        foreach (var (name, vk) in Configuration.DefaultKeyBindings())
        {
            if (!config.KeyBindings.ContainsKey(name))
                config.KeyBindings[name] = vk;
        }

        foreach (var (name, flag) in Configuration.DefaultControllerBindings())
        {
            if (!config.ControllerBindings.ContainsKey(name))
                config.ControllerBindings[name] = flag;
        }

        var pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        coreManager = new CoreManager(
            pluginDir,
            (msg, args) => log.Information(msg, args),
            (msg, args) => log.Error(msg, args));

        // Restore the previously selected core, or pick the default. A plugin
        // update changes the install directory, so a stale absolute path falls
        // back to matching the core by file name rather than losing the choice.
        selectedCore = !string.IsNullOrEmpty(config.SelectedCorePath)
            ? coreManager.FindByPath(config.SelectedCorePath)
              ?? coreManager.FindByFileName(Path.GetFileName(config.SelectedCorePath))
            : null;
        selectedCore ??= coreManager.GetDefault();

        romBrowser = new RomBrowser(config, SelectRom, GetRomExtensions);
        xivAuth = new XivAuthService(config, msg => log.Information("[XIVAuth] {Msg}", msg), SaveConfig);
        xivAuth.StateChanged += () =>
        {
            // Roster is per relay identity — re-fetch it whenever the login
            // state (or character) changes.
            friendsFetched = false;
            if (xivAuth.IsLoggedIn)
                TryRegisterPlayerId(auto: true);
            else
                netplayPanel?.StopForAuthLoss();
        };
        streamConfig = config.GetStreamConfig();
        streamPanel = new StreamPanel(
            streamConfig, textureProvider,
            msg => log.Information("[Stream] {Msg}", msg),
            () => core);
        streamPanel.SetVolume(config.Volume);
        presence = new RelayPresence(msg => log.Information("[Presence] {Msg}", msg),
            RegisterPlayerIdAsync);
        dxScreen = new Rendering.DxWorldRenderer(interop, log);
        netplayPanel = new NetplayPanel(
            config,
            msg => log.Information("[Netplay] {Msg}", msg),
            () => core,
            inputManager,
            xivAuth.GetPlayerUid);
        worldScreen = new WorldScreenRenderer(
            gameGui, textureProvider, streamConfig,
            GetPlayerPos, GetPlayerRot, GetNearbyPlayerPositions,
            config.ScreenPosition,
            pos =>
            {
                config.ScreenPosition = pos.Length is 3 or 6 ? pos : null;
                SaveConfig();
                PublishLocalScreenState();
            });

        framework.Update += OnFrameworkUpdate;
    }

    private Vector3? GetPlayerPos() => objectTable.LocalPlayer?.Position;
    private float GetPlayerRot() => objectTable.LocalPlayer?.Rotation ?? 0f;

    private WorldScreenState? CreateLocalScreenState()
    {
        if (worldScreen is not { IsPlaced: true })
            return null;

        // Legacy placements may not have a stored normal until their first
        // render. Resolve it before publishing so every viewer gets a fixed
        // surface orientation rather than a camera-facing billboard.
        var cameraPosition = GetPlayerPos() ?? worldScreen.ScreenPosition + Vector3.UnitZ;
        worldScreen.GetQuadBasis(cameraPosition, out _, out _);

        var position = worldScreen.ScreenPosition;
        var normal = worldScreen.SurfaceNormal;
        var saved = normal is { } n
            ? new[] { position.X, position.Y, position.Z, n.X, n.Y, n.Z }
            : new[] { position.X, position.Y, position.Z };
        return new WorldScreenState
        {
            Position = saved,
            Width = config.ScreenWidth,
            Aspect = CoreAspect(),
        };
    }

    // Display aspect of the running game for world screens and stream
    // metadata; zero keeps the classic 3:2 until a core declares otherwise.
    private float CoreAspect() =>
        core is { IsGameLoaded: true } && core.AspectRatio > 0 ? (float)core.AspectRatio : 0f;

    private void PublishLocalScreenState() => streamPanel?.PublishScreenState(CreateLocalScreenState());

    // A placement can be made while the host WebSocket is still negotiating
    // its go-live acknowledgement. In that narrow window StreamHost retains
    // the state locally but cannot send it yet, leaving new spectators with a
    // valid video stream and no world placement. Reconcile after IsLive turns
    // true, then only publish again when the host-owned state changes.
    private void SynchronizeLiveScreenState()
    {
        if (streamPanel is not { IsLive: true })
        {
            lastPublishedLiveScreenState = null;
            return;
        }

        var current = CreateLocalScreenState();
        if (WorldScreenStatesEqual(current, lastPublishedLiveScreenState))
            return;

        streamPanel.PublishScreenState(current);
        lastPublishedLiveScreenState = current?.Clone();
    }

    private static bool WorldScreenStatesEqual(WorldScreenState? left, WorldScreenState? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Width != right.Width || left.Aspect != right.Aspect)
            return false;
        if (left.Position is null || right.Position is null)
            return left.Position is null && right.Position is null;
        if (left.Position.Length != right.Position.Length)
            return false;

        for (var i = 0; i < left.Position.Length; i++)
        {
            if (left.Position[i] != right.Position[i])
                return false;
        }

        return true;
    }

    private System.Collections.Generic.List<Vector3> GetNearbyPlayerPositions()
    {
        var positions = new System.Collections.Generic.List<Vector3>();
        var local = objectTable.LocalPlayer;
        foreach (var obj in objectTable)
        {
            if (obj != local && obj.Position != Vector3.Zero)
                positions.Add(obj.Position);
        }
        return positions;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void SetFocused(bool isFocused) => focused = isFocused;

    // Pause emulation and audio when the window is closed; resume when reopened.
    private bool windowOpen = true;
    public void SetWindowOpen(bool open)
    {
        if (open == windowOpen) return;
        windowOpen = open;

        if (!open)
        {
            if (core != null) core.Paused = true;
            StopAudio();
        }
        else if (screenOn && core != null)
        {
            core.Paused = false;
            if (audio == null && core.IsGameLoaded)
            {
                audio = new AudioPlayer(core, config);
                audio.Start();
            }
        }
    }

    private bool lastGamepadConnected;

    private void OnFrameworkUpdate(IFramework fw)
    {
        // Poll gamepad on the UI thread (WinRT requirement).
        inputManager.PollGamepad();

        var gpConnected = inputManager.Gamepad.Connected;
        if (gpConnected != lastGamepadConnected)
        {
            lastGamepadConnected = gpConnected;
            log.Information("[Input] Controller {State} ({Detail})",
                gpConnected ? "connected" : "disconnected",
                inputManager.Gamepad.DebugInfo);
        }

        if (core is not { IsGameLoaded: true })
        {
            inputManager.SuppressGameInput(false);
            return;
        }

        inputManager.SuppressGameInput(focused);

        if (core.ShutdownRequested)
        {
            core.UnloadGame();
            StopAudio();
            status = "Game stopped.";
        }
    }

    // --- Main window: piano-black TV shell, with the control deck docked right when open ---

    public void DrawMainWindow(ref bool show)
    {
        EnsureCoreLoaded();
        UpdatePresence();
        if (screenOn)
        {
            UpdateTexture();
        }

        if (core != null)
        {
            core.Paused = !screenOn;
        }

        var scale = Math.Clamp(config.ResolutionScale, 1, CurrentMaxScale);
        var baseW = core is { IsGameLoaded: true } ? core.BaseWidth : 256;
        var baseH = core is { IsGameLoaded: true } ? core.BaseHeight : 224;
        var screenW = baseW * scale;
        var screenH = baseH * scale;

        var innerW = screenW + (panelOpen ? PanelGap + PanelWidth : 0f);
        var innerH = screenH + ControlStrip;
        var windowW = Bezel * 2 + innerW;
        var windowH = Bezel * 2 + innerH;

        ImGui.SetNextWindowSize(new Vector2(windowW, windowH), ImGuiCond.Always);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##retro-main", ref show, flags))
        {
            focused = screenOn && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (inputManager.EscapeRequested)
            {
                CloseRequested?.Invoke();
            }

            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();

            // One clean graphite shell, with a single restrained accent.
            var shellMin = origin;
            var shellMax = origin + new Vector2(windowW, windowH);
            drawList.AddRectFilled(shellMin, shellMax, ShellBody, 12f);
            drawList.AddRectFilled(shellMin, new Vector2(shellMax.X, shellMin.Y + 12), ShellHighlight, 12f);
            drawList.AddRectFilled(shellMin, new Vector2(shellMax.X, shellMin.Y + 6), ShellBody, 12f);
            drawList.AddRect(shellMin + new Vector2(1, 1), shellMax - new Vector2(1, 1), GlossEdge, 11f, 0, 1f);
            drawList.AddRect(shellMin, shellMax, 0xB0EBC65D, 12f, 0, 1f);

            // Keep close at the true top-right rather than inside the bezel.
            var btnSize = new Vector2(16, 16);
            var btnPos = origin + new Vector2(windowW - btnSize.X - 7f, 5f);
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton("##close", btnSize))
            {
                show = false;
            }

            var btnHovered = ImGui.IsItemHovered();
            var btnCenter = btnPos + btnSize / 2;
            var x = 3.5f;
            var xColor = btnHovered ? NeonPink : TextDim;
            drawList.AddLine(btnCenter - new Vector2(x, x), btnCenter + new Vector2(x, x), xColor, 1.5f);
            drawList.AddLine(btnCenter + new Vector2(x, -x), btnCenter + new Vector2(-x, x), xColor, 1.5f);

            // Advance CRT power animation.
            var dt = ImGui.GetIO().DeltaTime;
            if (crtAnim < crtAnimTarget)
                crtAnim = Math.Min(crtAnim + dt * CrtAnimSpeed, 1f);
            else if (crtAnim > crtAnimTarget)
                crtAnim = Math.Max(crtAnim - dt * CrtAnimSpeed, 0f);

            var innerOrigin = origin + new Vector2(Bezel, Bezel);

            DrawCrt(innerOrigin, screenW, screenH);

            if (panelOpen)
            {
                var panelMin = innerOrigin + new Vector2(screenW + PanelGap, 0);
                var panelMax = innerOrigin + new Vector2(innerW, innerH);
                DrawControlDeck(panelMin, panelMax);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void DrawCrt(Vector2 glassMin, float screenW, float screenH)
    {
        var drawList = ImGui.GetWindowDrawList();

        var glassMax = glassMin + new Vector2(screenW, screenH);
        drawList.AddRectFilled(glassMin, glassMax, Black);

        drawList.PushClipRect(glassMin, glassMax, true);

        if (crtAnim <= 0f)
        {
            // Fully off — just the standby text, no content.
            CenteredText(drawList, glassMin, screenW, screenH, "STANDBY", 0xFF2A2A30);
        }
        else
        {
            // Ease the animation for a natural CRT feel.
            var t = crtAnim;
            var eased = t * t * (3f - 2f * t); // smoothstep

            // The visible band expands from the centre line.
            var centerY = glassMin.Y + screenH / 2f;
            var halfH = (screenH / 2f) * eased;
            var bandMin = new Vector2(glassMin.X, centerY - halfH);
            var bandMax = new Vector2(glassMax.X, centerY + halfH);

            if (eased < 0.3f)
            {
                // CRT warm-up: bright horizontal line at centre.
                var lineAlpha = (int)(0xFF * (eased / 0.3f));
                var lineH = Math.Max(2f, halfH);
                drawList.AddRectFilled(
                    new Vector2(glassMin.X, centerY - lineH),
                    new Vector2(glassMax.X, centerY + lineH),
                    (uint)(lineAlpha << 24 | lineAlpha << 16 | lineAlpha << 8 | 0xD0));
            }
            else
            {
                // Content reveal: clip to the expanding band.
                drawList.PushClipRect(bandMin, bandMax, true);

                if (texture != null && core is { IsGameLoaded: true })
                {
                    ImGui.SetCursorScreenPos(glassMin);
                    ImGui.Image(texture.Handle, new Vector2(screenW, screenH));
                }
                else
                {
                    CenteredText(drawList, glassMin, screenW, screenH, "NO SIGNAL", 0xFF585860);
                }

                if (config.Scanlines)
                {
                    for (var y = glassMin.Y; y < glassMax.Y; y += 3f)
                        drawList.AddLine(new Vector2(glassMin.X, y), new Vector2(glassMax.X, y), 0x40000000, 1f);
                }

                if (config.ApertureGrille)
                {
                    for (var x = glassMin.X; x < glassMax.X; x += 3f)
                        drawList.AddLine(new Vector2(x, glassMin.Y), new Vector2(x, glassMax.Y), 0x18000000, 1f);
                }

                if (config.ScreenGlow)
                    drawList.AddRectFilled(glassMin, glassMax, 0x0CFFFFFF, 0f);

                if (config.Vignette)
                {
                    const float vignetteDepth = 32f;
                    const int vignetteSteps = 8;
                    var step = vignetteDepth / vignetteSteps;
                    for (var i = 0; i < vignetteSteps; i++)
                    {
                        var inset = i * step;
                        var alpha = (int)(0x60 * (1f - i / (float)vignetteSteps));
                        drawList.AddRect(
                            glassMin + new Vector2(inset, inset),
                            glassMax - new Vector2(inset, inset),
                            (uint)(alpha << 24), 0f, 0, step + 1f);
                    }
                }

                if (config.ShowFps && core is { IsGameLoaded: true })
                {
                    var fpsText = $"{core.EmulationFps:0.0} FPS";
                    var fpsSize = ImGui.CalcTextSize(fpsText);
                    var fpsPos = new Vector2(glassMax.X - fpsSize.X - 6, glassMin.Y + 5);
                    drawList.AddText(fpsPos + new Vector2(1, 1), 0xCC000000, fpsText);
                    drawList.AddText(fpsPos, LedOn, fpsText);
                }

                drawList.PopClipRect(); // band clip

                // White flash that fades as the content settles.
                var flashT = (eased - 0.3f) / 0.7f;
                if (flashT < 1f)
                {
                    var fa = (int)(0xFF * (1f - flashT) * 0.4f);
                    drawList.AddRectFilled(bandMin, bandMax, (uint)(fa << 24 | 0x00FFFFFF));
                }
            }
        }

        drawList.PopClipRect(); // glass clip

        // Screen bezel — glossy black glass frame.
        drawList.AddRect(glassMin - new Vector2(3, 3), glassMax + new Vector2(3, 3), Black, 2f, 0, 6f);
        drawList.AddRect(glassMin - new Vector2(3, 3), glassMax + new Vector2(3, 3), GlossEdge, 2f, 0, 1f);
        drawList.AddRect(glassMin, glassMax, Black, 0f, 0, 2f);

        // Bottom control strip.
        var stripTop = glassMax.Y + (ControlStrip - 24f) / 2f;
        var midY = stripTop + 12f;

        // Power button — charcoal with a neon ring.
        const float powerSize = 24f;
        var powerMin = new Vector2(glassMin.X + 8, midY - powerSize / 2f);
        var powerMax = powerMin + new Vector2(powerSize, powerSize);
        drawList.AddRectFilled(powerMin, powerMax, GlossFill, 5f);
        drawList.AddRect(powerMin, powerMax, NeonCyan, 5f, 0, 1.5f);
        var powerCenter = powerMin + new Vector2(powerSize / 2f, powerSize / 2f);
        var iconColor = screenOn ? NeonCyan : NeonPink;
        drawList.AddCircle(powerCenter + new Vector2(0, 0.5f), 5f, iconColor, 12, 1.5f);
        drawList.AddLine(powerCenter + new Vector2(0, -7f), powerCenter + new Vector2(0, -1f), iconColor, 1.5f);

        ImGui.SetCursorScreenPos(powerMin);
        if (ImGui.InvisibleButton("##power", new Vector2(powerSize, powerSize)))
        {
            screenOn = !screenOn;
            crtAnimTarget = screenOn ? 1f : 0f;
        }

        // Power LED — turquoise glow when on, dim coral when off.
        var ledCenter = new Vector2(glassMin.X + 44, midY);
        var ledColor = screenOn ? NeonCyan : NeonPink;
        drawList.AddCircleFilled(ledCenter, 3f, ledColor, 12);
        drawList.AddCircle(ledCenter, 5f, ledColor & 0x40FFFFFF, 12, 2f); // soft glow halo

        // Brand badge — Solution Nine signage.
        const string brand = "R3TR0";
        var brandSize = ImGui.CalcTextSize(brand);
        var brandX = glassMin.X + (screenW - brandSize.X) / 2f;
        drawList.AddText(new Vector2(brandX, midY - brandSize.Y / 2f), NeonAmber, brand);

        // Decorative knobs — charcoal with neon indicator dots.
        foreach (var kx in new[] { glassMax.X - 56f, glassMax.X - 80f })
        {
            var c = new Vector2(kx, midY);
            drawList.AddCircleFilled(c, 7f, GlossFill, 18);
            drawList.AddCircle(c, 7f, GlossEdge, 18, 1.5f);
            drawList.AddLine(c, c - new Vector2(0, 5f), NeonViolet, 1.5f);
        }

        // Collapse/expand toggle — charcoal with a neon edge.
        const float collapseSize = 18f;
        var collapseCenter = new Vector2(glassMax.X - 16, midY);
        var collapseMin = collapseCenter - new Vector2(collapseSize / 2f, collapseSize / 2f);
        var collapseMax = collapseMin + new Vector2(collapseSize, collapseSize);
        drawList.AddRectFilled(collapseMin, collapseMax, GlossFill, 4f);
        drawList.AddRect(collapseMin, collapseMax, NeonCyan, 4f, 0, 1.5f);
        if (panelOpen)
        {
            drawList.AddTriangleFilled(
                collapseCenter + new Vector2(-3, -5),
                collapseCenter + new Vector2(-3, 5),
                collapseCenter + new Vector2(4, 0),
                TextBright);
        }
        else
        {
            drawList.AddTriangleFilled(
                collapseCenter + new Vector2(3, -5),
                collapseCenter + new Vector2(3, 5),
                collapseCenter + new Vector2(-4, 0),
                TextBright);
        }

        ImGui.SetCursorScreenPos(collapseMin);
        if (ImGui.InvisibleButton("##collapse", new Vector2(collapseSize, collapseSize)))
        {
            panelOpen = !panelOpen;
        }
    }

    private static void CenteredText(ImDrawListPtr drawList, Vector2 glassMin, float screenW, float screenH, string text, uint color)
    {
        var size = ImGui.CalcTextSize(text);
        drawList.AddText(glassMin + new Vector2((screenW - size.X) / 2f, (screenH - size.Y) / 2f), color, text);
    }

    private void DrawControlDeck(Vector2 panelMin, Vector2 panelMax)
    {
        var drawList = ImGui.GetWindowDrawList();
        var panelW = panelMax.X - panelMin.X;
        var panelH = panelMax.Y - panelMin.Y;

        // Opaque utility deck: readable at a glance over any in-game scene.
        drawList.AddRectFilled(panelMin, panelMax, DeckBody, 8f);
        drawList.AddRect(panelMin + new Vector2(1, 1), panelMax - new Vector2(1, 1), GlossEdge, 7f, 0, 1f);
        drawList.AddLine(panelMin + new Vector2(10f, 1f), panelMax - new Vector2(10f, panelH - 1f), 0x80EBC65D, 1f);

        var contentMin = panelMin + new Vector2(DeckContentInsetX, DeckContentInsetTop);
        var contentMax = panelMax - new Vector2(DeckContentInsetX, DeckContentInsetBottom);
        drawList.AddRect(contentMin, contentMax, 0x6046515C, 6f, 0, 1f);

        ImGui.SetCursorScreenPos(contentMin);
        PushPanelTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(TextContentInset, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 8f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild(
            "##sidepanel", contentMax - contentMin,
            false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();

        DrawDeckTabs();

        switch (selectedDeckTab)
        {
            case 0:
                BeginTabContent("##rom_tab_content");
                DrawRomTab();
                EndTabContent();
                break;

            case 1:
                BeginTabContent("##controls_tab_content");
                DrawInputStatus();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawInsetTextColored(new Vector4(0.36f, 0.78f, 0.92f, 1f), "Input bindings");
                DrawInsetTextDisabled("Select a field to rebind. Right-click restores its default.");
                ImGui.Spacing();
                DrawSectionHeading("Keyboard", $"{ButtonOrder.Length} inputs");
                DrawKeyboardTab();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawSectionHeading("Controller", $"{ControllerButtonOrder.Length} inputs");
                DrawControllerTab();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawSectionHeading("Analog sticks", "PS1 / PS2");
                DrawAnalogTab();
                EndTabContent();
                break;

            case 2:
                BeginTabContent("##settings_tab_content");
                DrawSettingsTab();
                EndTabContent();
                break;

            case 3:
                BeginTabContent("##sync_tab_content");
                DrawSyncSection();
                EndTabContent();
                break;

            case 4:
                BeginTabContent("##friends_tab_content");
                DrawFriendsTab();
                EndTabContent();
                break;
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(PanelThemeStyleVarCount);
        ImGui.PopStyleColor(PanelThemeColorCount);
    }

    private void DrawDeckTabs()
    {
        const float tabGap = 3f;
        var available = ImGui.GetContentRegionAvail().X;
        var tabWidth = Math.Max(1f,
            (available - tabGap * (DeckTabLabels.Length - 1)) / DeckTabLabels.Length);

        // These are navigation segments, not form fields: compact padding
        // preserves all labels while the equal widths fill the full deck.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 5f));
        for (var i = 0; i < DeckTabLabels.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0f, tabGap);

            var active = selectedDeckTab == i;
            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, 0xFF35424E);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xFF40505E);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, NeonCyan);
            }

            if (ImGui.Button($"{DeckTabLabels[i]}##deck_tab_{i}", new Vector2(tabWidth, 0f)))
            {
                selectedDeckTab = i;
                resetDeckContentScrollTab = i;
            }

            if (active)
                ImGui.PopStyleColor(3);
        }
        ImGui.PopStyleVar();
        ImGui.Separator();
    }

    private void BeginTabContent(string id)
    {
        ImGui.BeginChild(id, Vector2.Zero, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        if (resetDeckContentScrollTab == selectedDeckTab)
        {
            ImGui.SetScrollY(0f);
            resetDeckContentScrollTab = -1;
        }
    }

    private static void EndTabContent()
    {
        // Preserve a visible gutter after the last control when a tab scrolls
        // to the end, instead of stopping directly on the child border.
        ImGui.Dummy(new Vector2(0f, 12f));
        ImGui.EndChild();
    }

    // The tab content owns its shared margin, so text and fields stay on the
    // same vertical edge instead of relying on per-widget offsets.
    private static void DrawInsetText(string text)
    {
        ImGui.TextUnformatted(text);
    }

    private static void DrawInsetTextDisabled(string text)
    {
        ImGui.TextDisabled(text);
    }

    private static void DrawInsetTextColored(Vector4 color, string text)
    {
        ImGui.TextColored(color, text);
    }

    private static void DrawInsetTextWrapped(string text)
    {
        ImGui.TextWrapped(text);
    }

    private static void PushPanelTheme()
    {
        // Consistent dark application surfaces: controls read as fields and
        // actions, while the accent is reserved for state and focus.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, 0x00000000);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0xFF23272E);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0xFF2B3139);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0xFF35424E);
        ImGui.PushStyleColor(ImGuiCol.Button, 0xFF20252C);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xFF2B333C);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xFF35424E);
        ImGui.PushStyleColor(ImGuiCol.Header, 0xFF20252C);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0xFF2B333C);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0xFF35424E);
        ImGui.PushStyleColor(ImGuiCol.Text, TextBright);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextDim);
        ImGui.PushStyleColor(ImGuiCol.Separator, 0xFF39414B);
        ImGui.PushStyleColor(ImGuiCol.Tab, 0xFF171B20);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, 0xFF27313A);
        ImGui.PushStyleColor(ImGuiCol.TabActive, 0xFF35424E);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 0xFF121519);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, 0xFF3A444F);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, 0xFF4A5A68);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, NeonCyan);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, NeonCyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, NeonCyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, 0xFFFFD47A);
        ImGui.PushStyleColor(ImGuiCol.Border, 0xFF46515C);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, 0x804E9AC0);
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, NeonCyan);
    }

    private void DrawCoreSelector()
    {
        DrawInsetText("Core");

        if (coreManager.Cores.Count == 0)
        {
            DrawInsetTextWrapped(coreManager.ScanError);
            if (ImGui.Button("Rescan cores"))
            {
                coreManager.Scan();
                selectedCore ??= coreManager.GetDefault();
            }
            return;
        }

        var currentIndex = selectedCore != null ? coreManager.Cores.IndexOf(selectedCore) : -1;
        var preview = selectedCore?.DisplayName ?? "(select a core)";

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##coreselect", preview))
        {
            for (var i = 0; i < coreManager.Cores.Count; i++)
            {
                var c = coreManager.Cores[i];
                if (ImGui.Selectable(c.DisplayName, i == currentIndex))
                {
                    SwitchCore(c);
                }
            }

            ImGui.EndCombo();
        }

        if (selectedCore is { Extensions.Length: > 0 })
        {
            DrawInsetTextDisabled($"Supports: {string.Join(" ", selectedCore.Extensions)}");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rescan"))
        {
            var previousPath = selectedCore?.Path;
            coreManager.Scan();
            selectedCore = previousPath != null ? coreManager.FindByPath(previousPath) : null;
            selectedCore ??= coreManager.GetDefault();
        }
    }

    private void SwitchCore(CoreInfo newCore)
    {
        if (selectedCore == newCore)
        {
            return;
        }

        selectedCore = newCore;
        config.SelectedCorePath = newCore.Path;
        SaveConfig();

        // Tear down the running core so the next ROM load picks up the new one.
        StopAudio();
        core?.Dispose();
        core = null;
        coreLoadAttempted = false;
        texture?.Dispose();
        texture = null;
        lastFrameVersion = -1;
        screenOn = false;
        crtAnimTarget = 0f;
        status = $"Core selected: {newCore.DisplayName}. Load a ROM to start.";
    }

    private void DrawRomTab()
    {
        ImGui.Indent(8f);
        DrawCoreSelector();
        ImGui.Separator();
        DrawSaveStates();
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##rom", ref romPath, 1024))
        {
            config.RomDirectory = Path.GetDirectoryName(romPath) ?? config.RomDirectory;
        }

        if (ImGui.Button("Load ROM"))
        {
            LoadRom();
        }

        if (core is { IsGameLoaded: true })
        {
            ImGui.SameLine();
            if (ImGui.Button("Reset"))
            {
                core.Reset();
            }
        }

        ImGui.Separator();

        romBrowser.DrawContents();
        ImGui.Unindent(8f);
    }

    private void DrawSaveStates()
    {
        if (core is not { IsGameLoaded: true })
        {
            ImGui.TextWrapped("Load a game to use save states.");
            return;
        }

        if (!StatesSupported)
        {
            ImGui.TextWrapped("Save states are not available for PS2 games; progress saves to the memory card instead.");
            return;
        }

        ImGui.TextWrapped("4 save slots per game. The game also auto-saves when the plugin closes and resumes on load.");
        ImGui.Separator();

        for (var slot = 1; slot <= 4; slot++)
        {
            ImGui.TextUnformatted($"Slot {slot}");
            if (HasSaveState(slot))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(saved)");
            }

            ImGui.SameLine();
            if (ImGui.Button($"Save##slot{slot}"))
            {
                SaveStateToSlot(slot);
            }

            ImGui.SameLine();
            if (ImGui.Button($"Load##slot{slot}"))
            {
                LoadStateFromSlot(slot);
            }
        }

        ImGui.Spacing();
        ImGui.TextWrapped(saveStateStatus);
    }

    private string StatesDir => Path.Combine(pluginInterface.ConfigDirectory.FullName, "states");
    private string GameStatesDir => Path.Combine(StatesDir, gameKey);
    private string AutoSavePath => Path.Combine(GameStatesDir, "auto.state");
    private string SlotPath(int slot) => Path.Combine(GameStatesDir, $"slot{slot}.state");

    private bool HasSaveState(int slot) => !string.IsNullOrEmpty(gameKey) && File.Exists(SlotPath(slot));

    private void SaveStateToSlot(int slot)
    {
        if (core is not { IsGameLoaded: true })
        {
            return;
        }

        if (!StatesSupported)
        {
            saveStateStatus = "Save states are not available for this core.";
            return;
        }

        var data = core.SaveState();
        if (data == null)
        {
            saveStateStatus = "Save failed.";
            return;
        }

        try
        {
            Directory.CreateDirectory(GameStatesDir);
            File.WriteAllBytes(SlotPath(slot), data);
            saveStateStatus = $"Saved to slot {slot}.";
        }
        catch (Exception ex)
        {
            saveStateStatus = "Save failed.";
            log.Error(ex, "Failed to save state to slot {Slot}", slot);
        }
    }

    private void LoadStateFromSlot(int slot)
    {
        if (core is not { IsGameLoaded: true })
        {
            return;
        }

        if (!StatesSupported)
        {
            saveStateStatus = "Save states are not available for this core.";
            return;
        }

        var path = SlotPath(slot);
        if (!File.Exists(path))
        {
            saveStateStatus = $"Slot {slot} is empty.";
            return;
        }

        try
        {
            var data = File.ReadAllBytes(path);
            saveStateStatus = core.LoadState(data) ? $"Loaded slot {slot}." : "Load failed.";
        }
        catch (Exception ex)
        {
            saveStateStatus = "Load failed.";
            log.Error(ex, "Failed to load state from slot {Slot}", slot);
        }
    }

    private bool TryAutoLoad()
    {
        if (string.IsNullOrEmpty(gameKey))
        {
            return false;
        }

        // Never feed a stored state to a core that cannot round-trip it; for
        // LRPS2 the unserialize itself crashes the game process.
        if (!StatesSupported)
        {
            return false;
        }

        if (!File.Exists(AutoSavePath))
        {
            return false;
        }

        try
        {
            var data = File.ReadAllBytes(AutoSavePath);
            return core?.LoadState(data) == true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load auto-save");
            return false;
        }
    }

    private void AutoSave()
    {
        if (core is not { IsGameLoaded: true } || string.IsNullOrEmpty(gameKey))
        {
            return;
        }

        // Writing a state the core cannot load back would poison the next
        // boot for any plugin version that still auto-loads it.
        if (!StatesSupported)
        {
            return;
        }

        try
        {
            var data = core.SaveState();
            if (data == null)
            {
                return;
            }

            Directory.CreateDirectory(GameStatesDir);
            File.WriteAllBytes(AutoSavePath, data);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to auto-save state");
        }
    }

    private static readonly (uint Id, string Name)[] JoypadDebugNames =
    {
        (Libretro.JoypadUp, "Up"), (Libretro.JoypadDown, "Down"),
        (Libretro.JoypadLeft, "Left"), (Libretro.JoypadRight, "Right"),
        (Libretro.JoypadA, "A"), (Libretro.JoypadB, "B"),
        (Libretro.JoypadX, "X"), (Libretro.JoypadY, "Y"),
        (Libretro.JoypadL, "L"), (Libretro.JoypadR, "R"),
        (Libretro.JoypadL2, "L2"), (Libretro.JoypadR2, "R2"),
        (Libretro.JoypadL3, "L3"), (Libretro.JoypadR3, "R3"),
        (Libretro.JoypadStart, "Start"), (Libretro.JoypadSelect, "Select"),
    };

    // Live readout of the input pipeline: mode, window focus, controller
    // detection (and which backend sees it), and what the emulator is
    // actually receiving this frame.
    private void DrawInputStatus()
    {
        DrawSectionHeading("Status", "live");

        var modeName = config.InputMode switch
        {
            InputMode.Keyboard => "Keyboard only",
            InputMode.Controller => "Controller only",
            _ => "Both",
        };
        DrawInsetText($"Input mode: {modeName}");
        if (config.InputMode != InputMode.Both)
        {
            DrawInsetTextDisabled("Both is recommended; the other device is ignored entirely.");
        }

        DrawInsetText(focused
            ? "Window: focused — input drives the emulator"
            : "Window: not focused — click the screen first");

        var gp = inputManager.Gamepad;
        if (gp.Connected)
        {
            DrawInsetText($"Controller: {gp.ActiveBackend} "
                          + $"(L {gp.LeftStickX:F2}, {gp.LeftStickY:F2} | "
                          + $"R {gp.RightStickX:F2}, {gp.RightStickY:F2})");
        }
        else
        {
            DrawInsetTextColored(new Vector4(1f, 0.55f, 0.45f, 1f),
                "Controller: none detected");
            DrawInsetTextDisabled(gp.DebugInfo);
        }

        var live = inputManager.GetLocalJoypad();
        string seen;
        if (live == 0)
        {
            seen = "nothing";
        }
        else
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var (id, name) in JoypadDebugNames)
            {
                if ((live & (1 << (int)id)) != 0)
                {
                    names.Add(name);
                }
            }

            seen = string.Join(" ", names);
        }

        DrawInsetText($"Emulator sees: {seen}");

        if (selectedCore is { IsPs1: true } or { IsPs2: true })
        {
            var (slx, sly, srx, sry) = inputManager.GetAnalogSnapshot();
            DrawInsetText($"Analog to core: L {slx}, {sly} | R {srx}, {sry}");
            DrawInsetTextDisabled("Analog core: stick keys are bindable in the Analog sticks "
                + "section below; controller sticks and triggers map automatically.");
        }
    }

    private void DrawKeyboardTab()
    {
        DrawBindingColumnHeader();
        HandleKeyRebinding();

        foreach (var button in ButtonOrder)
        {
            var name = button.ToString();
            config.KeyBindings.TryGetValue(name, out var vk);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);
            ImGui.SameLine(BindingColumnX);

            var label = rebindingKey == button ? "Press a key..." : KeyName(vk);
            if (ImGui.Button($"{label}##kb{name}", new Vector2(-1, 0)))
            {
                rebindingKey = button;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                var defaults = Configuration.DefaultKeyBindings();
                if (defaults.TryGetValue(name, out var dvk))
                {
                    config.KeyBindings[name] = dvk;
                }

                rebindingKey = null;
                SaveConfig();
            }
        }
    }

    private void DrawControllerTab()
    {
        DrawBindingColumnHeader();
        HandleControllerRebinding();

        foreach (var button in ControllerButtonOrder)
        {
            var name = button.ToString();
            config.ControllerBindings.TryGetValue(name, out var flag);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);
            ImGui.SameLine(BindingColumnX);

            var label = rebindingController == button ? "Press button..." : XInputButtonName(flag);
            if (ImGui.Button($"{label}##cb{name}", new Vector2(-1, 0)))
            {
                inputManager.Gamepad.Poll();
                rebindingController = button;
                controllerRebindBaseline = inputManager.Gamepad.Buttons;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                var defaults = Configuration.DefaultControllerBindings();
                if (defaults.TryGetValue(name, out var dflag))
                {
                    config.ControllerBindings[name] = dflag;
                }

                rebindingController = null;
                SaveConfig();
            }
        }
    }

    private static readonly System.Collections.Generic.Dictionary<SnesButton, string> StickDirectionNames = new()
    {
        [SnesButton.LeftStickUp] = "Up",
        [SnesButton.LeftStickDown] = "Down",
        [SnesButton.LeftStickLeft] = "Left",
        [SnesButton.LeftStickRight] = "Right",
        [SnesButton.RightStickUp] = "Up",
        [SnesButton.RightStickDown] = "Down",
        [SnesButton.RightStickLeft] = "Left",
        [SnesButton.RightStickRight] = "Right",
    };

    // Keyboard bindings for the analog sticks (PS1/PS2). Controller sticks
    // and triggers are physical and map automatically, so they are not
    // rebindable here. Reuses the keyboard rebind flow.
    private void DrawAnalogTab()
    {
        DrawInsetTextDisabled("These keys drive the sticks on analog games. A controller's "
            + "physical sticks and LT/RT always map to the sticks and L2/R2 pressure.");
        ImGui.Spacing();
        DrawBindingColumnHeader();

        DrawInsetText("Left stick");
        foreach (var button in LeftStickOrder)
            DrawAnalogBindRow(button);

        ImGui.Spacing();
        DrawInsetText("Right stick");
        foreach (var button in RightStickOrder)
            DrawAnalogBindRow(button);
    }

    private void DrawAnalogBindRow(SnesButton button)
    {
        var name = button.ToString();
        config.KeyBindings.TryGetValue(name, out var vk);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(StickDirectionNames[button]);
        ImGui.SameLine(BindingColumnX);

        var label = rebindingKey == button ? "Press a key..." : KeyName(vk);
        if (ImGui.Button($"{label}##kb{name}", new Vector2(-1, 0)))
        {
            rebindingKey = button;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var defaults = Configuration.DefaultKeyBindings();
            if (defaults.TryGetValue(name, out var dvk))
            {
                config.KeyBindings[name] = dvk;
            }

            rebindingKey = null;
            SaveConfig();
        }
    }

    private static void DrawBindingColumnHeader()
    {
        ImGui.TextDisabled("CONTROL");
        ImGui.SameLine(BindingColumnX);
        ImGui.TextDisabled("ASSIGNED INPUT");
        ImGui.Separator();
    }

    private static void DrawSectionHeading(string title, string detail)
    {
        ImGui.TextColored(new Vector4(0.72f, 0.78f, 0.84f, 1f), title);
        ImGui.SameLine();
        ImGui.TextDisabled(detail);
    }

    private void DrawSettingsTab()
    {
        DrawInsetText("Options");

        var volume = (int)Math.Round(config.Volume * 100f);
        if (ImGui.SliderInt("Master volume", ref volume, 0, 100))
        {
            SetVolume(volume / 100f);
            SaveConfig();
        }
        DrawInsetTextDisabled("Applies to emulator and remote stream audio.");

        const string resolutionLabel = "Resolution scale";
        var maxScale = CurrentMaxScale;
        var resolutionWidth = Math.Max(140f,
            ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(resolutionLabel).X - 12f);
        ImGui.SetNextItemWidth(resolutionWidth);
        if (ImGui.BeginCombo(resolutionLabel, $"{Math.Clamp(config.ResolutionScale, 1, maxScale)}x"))
        {
            for (var i = 1; i <= MaxScale; i++)
            {
                var overCap = i > maxScale;
                if (ImGui.Selectable($"{i}x", i == config.ResolutionScale,
                        overCap ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                {
                    config.ResolutionScale = i;
                    SaveConfig();
                }
            }

            ImGui.EndCombo();
        }

        if (maxScale < MaxScale)
        {
            DrawInsetTextDisabled($"PS2 is capped at {Ps2MaxScale}x resolution scale.");
        }

        var showFps = config.ShowFps;
        if (ImGui.Checkbox("Show FPS overlay", ref showFps))
        {
            config.ShowFps = showFps;
            SaveConfig();
        }

        ImGui.Spacing();
        DrawInsetText("Input");
        ImGui.SetNextItemWidth(-1);
        var modeNames = new[] { "Both", "Keyboard only", "Controller only" };
        var currentMode = (int)config.InputMode;
        if (ImGui.Combo("##inputmode", ref currentMode, modeNames, modeNames.Length))
        {
            config.InputMode = (InputMode)currentMode;
            SaveConfig();
        }

        ImGui.Spacing();
        DrawInsetText("World screen");

        var width = config.ScreenWidth;
        if (ImGui.SliderFloat("Screen width", ref width, 0.5f, 20f, "%.1f yalms"))
        {
            config.ScreenWidth = width;
            streamConfig.ScreenWidth = width;
            SaveConfig();
            PublishLocalScreenState();
        }

        var dxWorldScreen = config.UseDxWorldScreen;
        if (ImGui.Checkbox("Use depth occlusion", ref dxWorldScreen))
        {
            config.UseDxWorldScreen = dxWorldScreen;
            SaveConfig();
        }
        if (dxScreen is { Failed: true })
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "Depth rendering could not initialise; the in-world video is disabled to preserve occlusion.");

        ImGui.Spacing();
        DrawInsetText("CRT effects");

        var scanlines = config.Scanlines;
        if (ImGui.Checkbox("Scanlines", ref scanlines))
        {
            config.Scanlines = scanlines;
            SaveConfig();
        }

        var grille = config.ApertureGrille;
        if (ImGui.Checkbox("Aperture grille", ref grille))
        {
            config.ApertureGrille = grille;
            SaveConfig();
        }

        var vignette = config.Vignette;
        if (ImGui.Checkbox("Vignette", ref vignette))
        {
            config.Vignette = vignette;
            SaveConfig();
        }

        var glow = config.ScreenGlow;
        if (ImGui.Checkbox("Screen glow", ref glow))
        {
            config.ScreenGlow = glow;
            SaveConfig();
        }
    }

    private void HandleKeyRebinding()
    {
        if (rebindingKey == null)
        {
            return;
        }

        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                config.KeyBindings[rebindingKey.Value.ToString()] = vk;
                rebindingKey = null;
                SaveConfig();
                return;
            }
        }
    }

    private void HandleControllerRebinding()
    {
        if (rebindingController == null)
        {
            return;
        }

        inputManager.Gamepad.Poll();
        var current = inputManager.Gamepad.Buttons;
        foreach (var flag in XInputButtonFlags)
        {
            if ((current & flag) != 0 && (controllerRebindBaseline & flag) == 0)
            {
                config.ControllerBindings[rebindingController.Value.ToString()] = flag;
                rebindingController = null;
                SaveConfig();
                return;
            }
        }
    }

    private static string KeyName(int vk) => vk == 0 ? "(none)" : ((VirtualKey)vk).ToString();

    private static string XInputButtonName(int flag) => flag switch
    {
        GamepadReader.A => "A",
        GamepadReader.B => "B",
        GamepadReader.X => "X",
        GamepadReader.Y => "Y",
        GamepadReader.LeftShoulder => "LB",
        GamepadReader.RightShoulder => "RB",
        GamepadReader.Start => "Start",
        GamepadReader.Back => "Back",
        GamepadReader.DPadUp => "DPad Up",
        GamepadReader.DPadDown => "DPad Down",
        GamepadReader.DPadLeft => "DPad Left",
        GamepadReader.DPadRight => "DPad Right",
        GamepadReader.LeftThumb => "L3",
        GamepadReader.RightThumb => "R3",
        _ => "(none)",
    };

    // --- Core / video / ROM plumbing ---

    private static readonly string[] FallbackExtensions = { ".sfc", ".smc", ".fig", ".swc", ".bs" };

    private string[] GetRomExtensions() =>
        selectedCore is { Extensions.Length: > 0 } ? selectedCore.Extensions : FallbackExtensions;

    private void UpdateTexture()
    {
        if (core == null)
        {
            return;
        }

        var version = core.FrameVersion;
        if (version == lastFrameVersion)
        {
            return;
        }

        if (!core.TryGetFrame(out var rgba, out var width, out var height))
        {
            return;
        }

        try
        {
            var spec = RawImageSpecification.Rgba32(width, height);
            var newTexture = textureProvider.CreateFromRaw(spec, rgba, "RetroXIV.Frame");
            texture?.Dispose();
            texture = newTexture;
            textureWidth = width;
            textureHeight = height;
            lastFrameVersion = version;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to upload emulator frame to a texture.");
        }
    }

    private void EnsureCoreLoaded()
    {
        if (core != null || coreLoadAttempted)
        {
            return;
        }

        coreLoadAttempted = true;

        // Priority: manual override in config → selected core from the dropdown → first discovered core.
        var path = !string.IsNullOrEmpty(config.CorePath) && File.Exists(config.CorePath)
            ? config.CorePath
            : selectedCore?.Path ?? string.Empty;
        LoadCoreFrom(path);
    }

    private void LoadCoreFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                status = $"Core not found: {path}";
                return;
            }

            StopAudio();
            core?.Dispose();
            texture?.Dispose();
            texture = null;
            lastFrameVersion = -1;

            var dataDir = pluginInterface.ConfigDirectory;
            var systemDir = Path.Combine(dataDir.FullName, "system");
            var saveDir = Path.Combine(dataDir.FullName, "saves");
            Directory.CreateDirectory(systemDir);
            Directory.CreateDirectory(saveDir);
            // LRPS2 looks for PS2 BIOS dumps here; the plugin creates the
            // layout but never ships BIOS (user-provided legal dumps only).
            Directory.CreateDirectory(Path.Combine(systemDir, "pcsx2", "bios"));
            InstallBundledPs2GameIndex(systemDir);

            hwRender?.Dispose();
            hwRender = D3D11HwContext.TryCreate();
            if (hwRender == null)
            {
                log.Warning("No D3D11 hardware render context; hardware-rendering cores will not load");
            }

            var newCore = new RetroCore
            {
                SystemDirectory = systemDir,
                SaveDirectory = saveDir,
                HwRender = hwRender,
                InputState = inputManager.GetInputState,
                BackgroundError = ex => log.Error(ex,
                    "Emulator thread stopped after a contained core failure"),
                TeardownWarning = msg =>
                {
                    log.Warning("[Core] {Msg}", msg);
                    status = msg;
                },
                LogReceived = (level, text) =>
                {
                    switch (level)
                    {
                        case Libretro.LogLevelError: log.Error("[core] {Text}", text); break;
                        case Libretro.LogLevelWarn: log.Warning("[core] {Text}", text); break;
                        case Libretro.LogLevelInfo: log.Information("[core] {Text}", text); break;
                        default: log.Debug("[core] {Text}", text); break;
                    }
                },
            };
            newCore.Load(path);
            core = newCore;

            status = $"Core loaded: {core.LibraryName} {core.LibraryVersion}";
        }
        catch (Exception ex)
        {
            status = $"Failed to load core: {ex.Message}";
            log.Error(ex, "Failed to load the libretro core from {Path}", path);
        }
    }

    private void InstallBundledPs2GameIndex(string systemDirectory)
    {
        var pluginDirectory = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrEmpty(pluginDirectory))
        {
            return;
        }

        var bundled = Path.Combine(pluginDirectory, "resources", "GameIndex.yaml");
        if (!File.Exists(bundled))
        {
            return;
        }

        var destinationDirectory = Path.Combine(systemDirectory, "pcsx2", "resources");
        var destination = Path.Combine(destinationDirectory, "GameIndex.yaml");
        Directory.CreateDirectory(destinationDirectory);

        if (!File.Exists(destination)
            || File.GetLastWriteTimeUtc(bundled) > File.GetLastWriteTimeUtc(destination))
        {
            File.Copy(bundled, destination, overwrite: true);
        }
    }

    public void SelectRom(string path)
    {
        romPath = path;
        config.RomDirectory = Path.GetDirectoryName(path) ?? config.RomDirectory;
        LoadRom();
    }

    private void LoadRom()
    {
        try
        {
            EnsureCoreLoaded();
            if (core == null)
            {
                status = "Core is not loaded.";
                return;
            }

            if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
            {
                status = "ROM path is not a valid file.";
                return;
            }

            // Refuse content the selected core cannot run — a stale selection
            // or a noisy extension list (BlastEm's bin/iso) must not hand a
            // foreign ROM, or a 474 MB disc image, to the wrong core.
            if (selectedCore is { Extensions.Length: > 0 }
                && !string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase)
                && !Array.Exists(selectedCore.Extensions, ext =>
                       string.Equals(ext, Path.GetExtension(romPath), StringComparison.OrdinalIgnoreCase)))
            {
                status = $"{Path.GetFileName(romPath)} is not a {selectedCore.DisplayName} ROM "
                         + $"(supports: {string.Join(" ", selectedCore.Extensions)}).";
                return;
            }

            var resolvedPath = ResolveRomPath(romPath);
            if (core.LoadGame(resolvedPath))
            {
                // Beetle PSX needs its DualShock subclass plugged in before
                // the first playable frames; other cores either poll what
                // they need (LRPS2) or default to the RetroPad.
                if (selectedCore?.PortDevice is { } portDevice)
                {
                    core.SetControllerPortDevice(0, portDevice);
                    core.SetControllerPortDevice(1, portDevice);
                }

                screenOn = true;
                crtAnimTarget = 1f;
                StopAudio();

                // The local world screen shows whatever the current game
                // declares (PS1's 4:3 instead of the SNES-era 3:2).
                if (worldScreen != null)
                {
                    worldScreen.Aspect = core.AspectRatio > 0
                        ? (float)core.AspectRatio
                        : WorldScreenRenderer.DefaultAspect;
                }

                gameKey = Path.GetFileNameWithoutExtension(resolvedPath);
                core.PreFrame = () =>
                {
                    inputManager.UpdateInputForEmulator(focused);
                    var np = netplayPanel?.Session;
                    if (np is { IsConnected: true, PeerConnected: true })
                    {
                        var local = inputManager.GetLocalJoypad();
                        var remote = np.SyncFrame(local);
                        inputManager.SetRemoteInput(remote);
                    }
                };

                var resumed = TryAutoLoad();

                for (var i = 0; i < 16; i++)
                {
                    core.RunFrame();
                }

                audio = new AudioPlayer(core, config);
                audio.Start();
                core.Start();

                status = resumed
                    ? $"Resumed: {Path.GetFileName(romPath)}"
                    : $"Running: {Path.GetFileName(romPath)} ({core.BaseWidth}x{core.BaseHeight} @ {core.Fps:0.##}fps)";
            }
            else
            {
                status = core.LastLoadError ?? "The core refused to load this ROM.";
            }
        }
        catch (Exception ex)
        {
            status = $"Failed to load ROM: {ex.Message}";
            log.Error(ex, "Failed to load ROM {Path}", romPath);
        }
    }

    private string ResolveRomPath(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return selectedCore is { NeedFullpath: true }
                ? ExtractDiscZip(path)
                : ExtractRomFromZip(path);
        }

        return path;
    }

    // Disc cores load by path and cue/bin sets need their siblings, so the
    // whole archive is unpacked into a stable per-zip directory and the best
    // matching entry (playlist first, then the largest image) is loaded.
    private string ExtractDiscZip(string zipPath)
    {
        var extractDir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "temp",
            Path.GetFileNameWithoutExtension(zipPath));
        Directory.CreateDirectory(extractDir);

        using var archive = ZipFile.OpenRead(zipPath);
        var extensions = selectedCore?.Extensions ?? [];

        ZipArchiveEntry? best = null;
        foreach (var e in archive.Entries)
        {
            var ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (Array.IndexOf(extensions, ext) < 0)
            {
                continue;
            }

            if (ext is ".m3u" or ".cue")
            {
                best = e;
                break;
            }

            if (best == null || e.Length > best.Length)
            {
                best = e;
            }
        }

        if (best == null)
        {
            throw new InvalidOperationException(
                $"No {selectedCore?.DisplayName ?? "compatible"} disc image found inside the archive.");
        }

        var target = Path.Combine(extractDir, best.Name);
        if (!File.Exists(target) || new FileInfo(target).Length != best.Length)
        {
            status = $"Extracting {Path.GetFileName(zipPath)}...";
            foreach (var e in archive.Entries)
            {
                if (e.Length == 0)
                {
                    continue;
                }

                e.ExtractToFile(Path.Combine(extractDir, e.Name), overwrite: true);
            }
        }

        return target;
    }

    private string ExtractRomFromZip(string zipPath)
    {
        var tempDir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "temp");
        Directory.CreateDirectory(tempDir);

        using var archive = ZipFile.OpenRead(zipPath);

        ZipArchiveEntry? entry = null;
        foreach (var e in archive.Entries)
        {
            if (IsRomFile(e.Name))
            {
                entry = e;
                break;
            }
        }

        // Cores that declare no extensions keep the legacy single-entry guess;
        // otherwise an archive without a ROM for the selected core fails here
        // instead of feeding foreign content to the core.
        if (entry == null && archive.Entries.Count == 1
            && selectedCore is not { Extensions.Length: > 0 })
        {
            entry = archive.Entries[0];
        }

        if (entry == null)
        {
            throw new InvalidOperationException(
                $"No {selectedCore?.DisplayName ?? "compatible"} ROM found inside the archive.");
        }

        var extractPath = Path.Combine(tempDir, entry.Name);
        entry.ExtractToFile(extractPath, overwrite: true);
        return extractPath;
    }

    private bool IsRomFile(string name)
    {
        var ext = Path.GetExtension(name);
        return Array.Exists(GetRomExtensions(), e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public void SetVolume(float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        config.Volume = clamped;
        audio?.SetVolume(clamped);
        streamPanel?.SetVolume(clamped);
    }

    private void SaveConfig() => pluginInterface.SavePluginConfig(config);

    private void StopAudio()
    {
        audio?.Dispose();
        audio = null;
    }

    private void DrawIdentitySection()
    {
        ImGui.TextUnformatted("Player identity");

        if (xivAuth.IsLoggedIn)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"{config.PlayerCharacterName} ({config.PlayerWorld})");
            ImGui.TextDisabled($"Lodestone ID: {config.PlayerLodestoneId}");
            ImGui.TextWrapped("Your player ID is registered against this account — see the Sync section below.");

            if (ImGui.Button("Log out"))
            {
                xivAuth.Logout();
                SaveConfig();
            }
        }
        else if (xivAuth.IsPolling)
        {
            ImGui.TextWrapped(xivAuth.Status);
            ImGui.Spacing();
            ImGui.TextUnformatted($"Code: {xivAuth.UserCode}");

            if (ImGui.SmallButton("Reopen login page"))
                Dalamud.Utility.Util.OpenLink(xivAuth.LoginUrl);

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                xivAuth.Logout();
            }
        }
        else
        {
            ImGui.TextWrapped("Log in with XIVAuth to use your FFXIV character as your player identity for netplay and streaming.");

            if (ImGui.Button("Log in with XIVAuth"))
            {
                _ = System.Threading.Tasks.Task.Run(xivAuth.StartLoginAsync);
            }
        }

        if (!string.IsNullOrEmpty(xivAuth.Error))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), xivAuth.Error);
        }
    }

    private string syncIdInput = string.Empty;
    private string syncNameInput = string.Empty;

    // Kept directly above the roster so adding a friend starts with the
    // one piece of information the other player needs.
    private void DrawShareablePlayerId()
    {
        var uid = xivAuth.GetPlayerUid();
        var registered = xivAuth.IsLoggedIn
            && !string.IsNullOrEmpty(config.PlayerId)
            && config.PlayerIdUid == uid;

        ImGui.TextUnformatted("Your player ID");
        if (registered)
        {
            var copied = DateTime.UtcNow < idCopiedUntil;
            ImGui.SameLine();
            if (ImGui.Button($"{(copied ? "Copied" : config.PlayerId)}##copy_player_id", new Vector2(-1, 0)))
            {
                ImGui.SetClipboardText(config.PlayerId);
                idCopiedUntil = DateTime.UtcNow.AddSeconds(1.5);
            }

            DrawInsetTextDisabled(copied ? "Copied to clipboard." : "Click to copy and share it with friends.");
        }
        else
        {
            DrawInsetTextDisabled(!xivAuth.IsLoggedIn
                ? "Sign in with XIVAuth to get a shareable ID."
                : playerRegistering ? "Preparing your player ID..." : "Your player ID is not ready yet.");
        }
    }

    // Who is online right now (relay presence), with LIVE badges and a
    // one-click Watch for live players.  Synced friends get a star.
    private void DrawOnlineSection()
    {
        if (presence == null)
            return;

        var list = presence.GetOnline();

        ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), $"{list.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted(list.Count == 1 ? "player online" : "players online");
        if (!presence.IsConnected)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(connecting...)");
        }

        if (list.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.BeginChild("##online", new Vector2(0, Math.Min(160, list.Count * 22 + 8)), false);

        string? watchKey = null;
        string? stopKey = null;
        var friendKeys = new HashSet<string>(
            FriendsSnapshot().Select(f => StreamPanel.NormalizeId(f.Key)));

        foreach (var player in list)
        {
            var key = StreamPanel.NormalizeId(player.PlayerId);
            var isSelf = key == StreamPanel.NormalizeId(config.PlayerId);
            var isFriend = friendKeys.Contains(key);

            if (isFriend)
                ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), "*");
            else
                ImGui.TextUnformatted(" ");
            ImGui.SameLine();

            ImGui.TextUnformatted(string.IsNullOrEmpty(player.Name) ? key : player.Name);
            ImGui.SameLine();
            ImGui.TextDisabled(key);

            if (player.Live)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "LIVE");

                if (!isSelf)
                {
                    ImGui.SameLine();
                    var watching = streamPanel?.IsWatching(key) == true;
                    if (ImGui.SmallButton(watching ? $"Off##online_{key}" : $"On##online_{key}"))
                    {
                        if (watching) stopKey = key;
                        else watchKey = key;
                    }
                }
            }
        }

        ImGui.EndChild();

        if (watchKey != null && streamPanel != null)
            watchError = streamPanel.TryWatch(watchKey, out var err) ? null : err;
        if (stopKey != null)
            streamPanel?.StopWatching(stopKey);
    }

    private string syncFilter = string.Empty;

    // Presence is independent of the currently visible control-deck tab. The
    // Friends tab still needs the global online list after the Sync UI was
    // split out, so keep this lifecycle with the main plugin window rather
    // than the old Sync-tab renderer.
    private void UpdatePresence()
    {
        var uid = xivAuth.GetPlayerUid();
        var registered = xivAuth.IsLoggedIn
            && !string.IsNullOrEmpty(config.PlayerId)
            && config.PlayerIdUid == uid;

        if (registered)
        {
            presence?.Start(
                streamPanel?.RelayUrl ?? config.RelayUrl,
                uid, config.PlayerId, config.PlayerCharacterName);
        }
        else
        {
            presence?.Stop();
        }
    }

    private void DrawSyncSection()
    {
        var uid = xivAuth.GetPlayerUid();
        var registered = xivAuth.IsLoggedIn
            && !string.IsNullOrEmpty(config.PlayerId)
            && config.PlayerIdUid == uid;

        // Watching friends works without a login. A local account is only
        // required when the player wants to publish a stream.
        if (!registered && xivAuth.IsLoggedIn)
            TryRegisterPlayerId(auto: true);

        DrawStreamingControls(registered, uid);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawWorldScreenSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawNetplaySection();
    }

    private void DrawFriendsTab()
    {
        PollSyncStatus();
        DrawOnlinePlayerCount();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawShareablePlayerId();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawIdentitySection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawFriendRoster();
    }

    private void DrawOnlinePlayerCount()
    {
        var onlineCount = presence?.OnlineCount ?? 0;
        ImGui.TextColored(new Vector4(0.25f, 0.82f, 0.95f, 1f), $"{onlineCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted(onlineCount == 1 ? "player online" : "players online");

        if (presence is { IsConnected: false })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(connecting...)");
        }
    }

    private void DrawNetplaySection()
    {
        DrawInsetText("Netplay");
        if (!xivAuth.IsLoggedIn)
        {
            DrawInsetTextDisabled("Sign in with XIVAuth to host or join netplay.");
            return;
        }

        netplayPanel?.DrawTab();
    }

    private void DrawStreamingControls(bool registered, string uid)
    {
        DrawInsetText("Streaming");

        var showLocalScreen = config.ShowLocalWorldScreen;
        if (ImGui.Checkbox("Show local screen in world", ref showLocalScreen))
        {
            config.ShowLocalWorldScreen = showLocalScreen;
            SaveConfig();
        }
        if (!showLocalScreen)
            DrawInsetTextDisabled("Local screen is hidden. Placement is still available below.");

        if (streamPanel is { IsLive: true })
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "LIVE");
            ImGui.SameLine();
            ImGui.TextDisabled("Your stream is available to synced friends.");
            if (ImGui.Button("Stop stream", new Vector2(-1, 0)))
                streamPanel.StopLive();
            return;
        }

        if (registered && core is { IsGameLoaded: true })
        {
            if (ImGui.Button("Start stream", new Vector2(-1, 0)))
            {
                lastPublishedLiveScreenState = null;
                streamPanel?.GoLive(uid, config.PlayerCharacterName, config.PlayerId, CreateLocalScreenState());
            }
            return;
        }

        if (!xivAuth.IsLoggedIn)
        {
            DrawInsetTextDisabled("Sign in only when you want to share a stream.");
            if (ImGui.Button("Sign in to stream", new Vector2(-1, 0)))
                _ = System.Threading.Tasks.Task.Run(xivAuth.StartLoginAsync);
            return;
        }

        if (xivAuth.IsPolling)
        {
            DrawInsetTextDisabled("Finish sign-in in your browser, then return here.");
            if (ImGui.Button("Open sign-in page", new Vector2(-1, 0)))
                Dalamud.Utility.Util.OpenLink(xivAuth.LoginUrl);
            return;
        }

        if (playerRegistering)
        {
            DrawInsetTextDisabled("Preparing streaming access...");
            return;
        }

        if (!string.IsNullOrEmpty(playerRegError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "Streaming setup needs another try.");
            if (ImGui.Button("Retry setup", new Vector2(-1, 0)))
                TryRegisterPlayerId(auto: false);
            return;
        }

        DrawInsetTextDisabled("Load a game to start streaming.");
    }

    private void DrawFriendRoster()
    {
        EnsureFriendsSynced();

        System.Collections.Generic.Dictionary<string, LivePlayerInfo> liveSnapshot;
        lock (liveStatus)
            liveSnapshot = new System.Collections.Generic.Dictionary<string, LivePlayerInfo>(liveStatus);

        var roster = FriendsSnapshot();
        var friends = roster
            .Select((friend, index) =>
            {
                var key = StreamPanel.NormalizeId(friend.Key);
                liveSnapshot.TryGetValue(key, out var live);
                var label = !string.IsNullOrWhiteSpace(friend.Name) ? friend.Name
                    : !string.IsNullOrWhiteSpace(live?.Name) ? live!.Name : key;
                return new { Friend = friend, Index = index, Key = key, Live = live, Label = label };
            })
            .Where(friend => string.IsNullOrWhiteSpace(syncFilter)
                || friend.Label.Contains(syncFilter, StringComparison.OrdinalIgnoreCase)
                || friend.Key.Contains(syncFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(friend => friend.Live != null)
            .ThenBy(friend => friend.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var liveCount = liveSnapshot.Count;
        ImGui.TextUnformatted("Friends");
        ImGui.SameLine();
        ImGui.TextDisabled($"{roster.Count} synced | {liveCount} live | "
            + $"{streamPanel?.WatchCount ?? 0}/{StreamPanel.MaxStreams} streams active");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##syncfilter", "Search friends", ref syncFilter, 128);

        string? watchKey = null;
        string? stopKey = null;
        string? removeKey = null;

        var listHeight = Math.Min(260f, Math.Max(94f, friends.Count * 30f + 8f));
        ImGui.BeginChild("##synced_friends", new Vector2(0, listHeight), true);
        if (friends.Count == 0)
        {
            DrawInsetTextDisabled(roster.Count == 0
                ? "No friends synced yet. Add a player ID below."
                : "No synced friends match your search.");
        }

        foreach (var friend in friends)
        {
            var watching = streamPanel?.IsWatching(friend.Key) == true;
            var streamEnabled = watching;
            var canStart = friend.Live != null || watching;

            ImGui.PushID($"friend_{friend.Index}");
            if (!canStart)
                ImGui.BeginDisabled();
            if (ImGui.Checkbox("##stream_enabled", ref streamEnabled))
            {
                if (streamEnabled)
                    watchKey = friend.Key;
                else
                    stopKey = friend.Key;
            }
            if (!canStart)
                ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered())
                ImGui.SetTooltip("This friend is not streaming right now.");

            ImGui.SameLine();
            ImGui.TextUnformatted(friend.Label);
            ImGui.SameLine();
            if (friend.Live != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "LIVE");
                ImGui.SameLine();
                ImGui.TextDisabled($"{friend.Live.Viewers} watching");
            }
            else
            {
                ImGui.TextDisabled("offline");
            }

            ImGui.SameLine();
            ImGui.TextDisabled(friend.Key);
            if (watching)
            {
                ImGui.SameLine();
                var visible = streamPanel?.IsWindowVisible(friend.Key) == true;
                if (ImGui.SmallButton(visible ? "Hide window" : "Show window"))
                    streamPanel?.SetWindowVisible(friend.Key, !visible);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                removeKey = friend.Key;
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();

        if (!string.IsNullOrEmpty(watchError))
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), watchError);

        if (removeKey != null)
        {
            var removedKey = StreamPanel.NormalizeId(removeKey);
            streamPanel?.StopWatching(removedKey);
            RemoveFriendEntry(removedKey);
        }

        if (watchKey != null && streamPanel != null)
            watchError = streamPanel.TryWatch(watchKey, out var error) ? null : error;
        if (stopKey != null)
        {
            streamPanel?.StopWatching(stopKey);
            watchError = null;
        }
        ImGui.Spacing();
        DrawInsetText("Add friend");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##syncid", "Player ID (example: K7QX-4MRT)", ref syncIdInput, 16))
            watchError = null;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##syncname", "Nickname (optional)", ref syncNameInput, 128);

        if (ImGui.Button("Add friend", new Vector2(-1, 0)) && !string.IsNullOrWhiteSpace(syncIdInput))
        {
            var normalized = StreamPanel.NormalizeId(syncIdInput);
            var idCore = PlayerIds.Digits(normalized);
            if (idCore.Length is < 6 or > 8)
            {
                watchError = "Player IDs use 6-8 letters or digits.";
            }
            else if (roster.Any(friend => StreamPanel.NormalizeId(friend.Key) == normalized))
            {
                watchError = "That player is already in your friends list.";
            }
            else
            {
                AddFriendEntry(normalized, syncNameInput.Trim());
                syncIdInput = string.Empty;
                syncNameInput = string.Empty;
                syncFilter = string.Empty;
                watchError = null;
            }
        }
    }

    private void DrawSyncSectionLegacy()
    {
        ImGui.TextUnformatted("Sync");

        var uid = xivAuth.GetPlayerUid();
        var registered = xivAuth.IsLoggedIn
            && !string.IsNullOrEmpty(config.PlayerId)
            && config.PlayerIdUid == uid;

        DrawShareablePlayerId();
        ImGui.Spacing();

        if (registered)
        {
            presence?.Start(
                streamPanel?.RelayUrl ?? config.RelayUrl,
                uid, config.PlayerId, config.PlayerCharacterName);
            DrawOnlineSection();
        }
        else
        {
            presence?.Stop();

            if (xivAuth.IsLoggedIn)
            {
                TryRegisterPlayerId(auto: true);
                if (!playerRegistering && !string.IsNullOrEmpty(playerRegError))
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Registration failed: {playerRegError}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Retry"))
                        TryRegisterPlayerId(auto: false);
                }
            }
            else
            {
                ImGui.TextWrapped("Watching friends needs no login.");
            }
        }

        ImGui.Spacing();

        // Go live / stop live.
        if (streamPanel is { IsLive: true })
        {
            var host = streamPanel.GetStreamHost();
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "LIVE");
            ImGui.SameLine();
            ImGui.TextWrapped(host?.Status ?? "");

            if (ImGui.Button("Stop streaming", new Vector2(-1, 0)))
            {
                streamPanel.StopLive();
            }
        }
        else if (!registered)
        {
            ImGui.TextWrapped("Go live once your player ID is registered.");
        }
        else
        {
            var backend = core;
            if (backend is { IsGameLoaded: true })
            {
                if (ImGui.Button("Go live", new Vector2(-1, 0)))
                {
                    streamPanel?.GoLive(uid, config.PlayerCharacterName, config.PlayerId, CreateLocalScreenState());
                }
            }
            else
            {
                ImGui.TextWrapped("Load a game to go live. Friends with your player ID can watch.");
            }
        }

        if (!string.IsNullOrEmpty(streamPanel?.GetStreamHost()?.VideoStatus) &&
            streamPanel.GetStreamHost()!.VideoStatus != "Not started")
        {
            ImGui.TextDisabled(streamPanel.GetStreamHost()!.VideoStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        PollSyncStatus();
        EnsureFriendsSynced();

        // Friend list.
        ImGui.TextUnformatted("Synced friends");
        ImGui.SameLine();
        ImGui.TextDisabled($"— watching {streamPanel?.WatchCount ?? 0}/{StreamPanel.MaxStreams} streams");

        var roster = FriendsSnapshot();
        if (roster.Count == 0)
        {
            ImGui.TextWrapped("No friends synced yet. Add a friend's player ID to watch their stream.");
        }

        string? removeKey = null;
        string? watchKey = null;
        string? stopKey = null;
        string? placeKey = null;

        for (var i = 0; i < roster.Count; i++)
        {
            var friend = roster[i];
            var key = StreamPanel.NormalizeId(friend.Key);

            LivePlayerInfo? live;
            lock (liveStatus)
                liveStatus.TryGetValue(key, out live);

            var label = !string.IsNullOrEmpty(friend.Name) ? friend.Name
                : !string.IsNullOrEmpty(live?.Name) ? live!.Name
                : key;

            ImGui.TextUnformatted(label);
            ImGui.SameLine();

            if (live != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "LIVE");
                ImGui.SameLine();
                ImGui.TextDisabled($"({live.Viewers} watching)");
                ImGui.SameLine();
            }

            var watching = streamPanel?.IsWatching(key) == true;
            if (watching)
            {
                if (ImGui.SmallButton($"Off##friend{i}"))
                    stopKey = key;

                ImGui.SameLine();
                var windowVisible = streamPanel?.IsWindowVisible(key) == true;
                if (ImGui.SmallButton($"{(windowVisible ? "Hide" : "Window")}##friend{i}"))
                    streamPanel?.SetWindowVisible(key, !windowVisible);

                ImGui.SameLine();
                if (ImGui.SmallButton($"Screen##friend{i}"))
                    placeKey = key;
            }
            else
            {
                ImGui.BeginDisabled(live == null);
                if (ImGui.SmallButton($"On##friend{i}"))
                    watchKey = key;
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"X##friend{i}"))
                removeKey = key;
        }

        if (!string.IsNullOrEmpty(watchError))
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), watchError);

        if (removeKey != null)
        {
            streamPanel?.StopWatching(removeKey);
            RemoveFriendEntry(removeKey);
        }

        if (watchKey != null && streamPanel != null)
        {
            watchError = streamPanel.TryWatch(watchKey, out var err) ? null : err;
        }

        if (stopKey != null)
        {
            streamPanel?.StopWatching(stopKey);
            watchError = null;
        }

        if (placeKey != null)
        {
            var renderer = GetOrCreateWatchScreen(placeKey);
            BeginPlacement(renderer);
        }

        ImGui.Spacing();

        // Add friend by player ID.
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##syncid", "Player ID (e.g. K7QX-4MRT)", ref syncIdInput, 16))
            watchError = null;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##syncname", "Name (optional)", ref syncNameInput, 128);

        if (ImGui.Button("Add friend") && !string.IsNullOrWhiteSpace(syncIdInput))
        {
            var normalized = StreamPanel.NormalizeId(syncIdInput);
            var core = PlayerIds.Digits(normalized);
            if (core.Length is < 6 or > 8)
            {
                watchError = "Player IDs are 6-8 letters/digits, e.g. K7QX-4MRT.";
            }
            else if (roster.Any(f => StreamPanel.NormalizeId(f.Key) == normalized))
            {
                watchError = "That player is already synced.";
            }
            else
            {
                AddFriendEntry(normalized, syncNameInput.Trim());
                syncIdInput = string.Empty;
                syncNameInput = string.Empty;
                watchError = null;
            }
        }
    }

    // Register (or re-register after a character change) this account's
    // player ID with the relay.  The relay issues the ID and ties it to the
    // XIVAuth identity, so streaming identity and login stay coupled.
    private void TryRegisterPlayerId(bool auto)
    {
        if (playerRegistering || !xivAuth.IsLoggedIn)
            return;

        var uid = xivAuth.GetPlayerUid();
        if (!string.IsNullOrEmpty(config.PlayerId) && config.PlayerIdUid == uid)
            return;

        // Auto-retries are throttled; the manual button always goes through.
        if (auto && (DateTime.UtcNow - lastRegisterAttempt).TotalSeconds < 30)
            return;

        lastRegisterAttempt = DateTime.UtcNow;
        playerRegistering = true;
        playerRegError = null;

        var relayUrl = streamPanel?.RelayUrl ?? config.RelayUrl;
        var name = config.PlayerCharacterName;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                if (await RegisterPlayerIdAsync(relayUrl, uid, name) == null)
                    playerRegError = "No response from relay";
            }
            catch (Exception ex)
            {
                playerRegError = ex.Message;
            }
            finally
            {
                playerRegistering = false;
            }
        });
    }

    // One register round-trip against the relay; returns the issued player ID
    // and persists it to config. Register is idempotent on the relay, which is
    // what lets RelayPresence heal a saved ID the relay no longer knows.
    private async System.Threading.Tasks.Task<string?> RegisterPlayerIdAsync(
        string relayUrl, string uid, string name)
    {
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(new Uri(relayUrl.TrimEnd('/') + "/ws"), cts.Token);

        var json = new ControlMsg { Action = "register", Uid = uid, Name = name }.ToJson();
        await ws.SendAsync(
            new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(json)),
            System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        var msg = result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
            ? ControlMsg.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count))
            : null;

        if (msg?.Type == "registered" && !string.IsNullOrEmpty(msg.PlayerId))
        {
            config.PlayerId = msg.PlayerId;
            config.PlayerIdUid = uid;
            SaveConfig();
            log.Information($"[Sync] player ID registered: {msg.PlayerId}");
            return msg.PlayerId;
        }

        playerRegError = msg?.Message ?? "No response from relay";
        return null;
    }

    // One text request → one text response against the relay.
    private static async System.Threading.Tasks.Task<ControlMsg?> RelayRoundTripAsync(
        string relayUrl, ControlMsg request)
    {
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(new Uri(relayUrl.TrimEnd('/') + "/ws"), cts.Token);
        await ws.SendAsync(
            new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(request.ToJson())),
            System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[16384];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        return result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
            ? ControlMsg.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count))
            : null;
    }

    private bool FriendsRelayAvailable => xivAuth.IsLoggedIn
        && !string.IsNullOrEmpty(config.PlayerId)
        && config.PlayerIdUid == xivAuth.GetPlayerUid();

    private List<SyncFriend> FriendsSnapshot()
    {
        lock (friendsLock)
        {
            return new List<SyncFriend>(syncFriends);
        }
    }

    private void ApplyFriendsList(List<FriendInfo> list)
    {
        lock (friendsLock)
        {
            syncFriends.Clear();
            foreach (var friend in list)
            {
                syncFriends.Add(new SyncFriend
                {
                    Key = StreamPanel.NormalizeId(friend.Key),
                    Name = friend.Name,
                });
            }

            config.SyncFriends = new List<SyncFriend>(syncFriends);
        }

        SaveConfig();
        lastSyncCheck = DateTime.MinValue; // poll live status for the new roster right away
    }

    // The relay is the source of truth for the roster; call once login is up.
    // Fetches the server list, applies any offline removals, uploads local-only
    // entries (covers offline adds and the one-time migration of rosters that
    // previously lived only in the client config), then adopts the server list.
    private void EnsureFriendsSynced()
    {
        if (friendsSyncInFlight || friendsFetched || !FriendsRelayAvailable)
            return;
        if ((DateTime.UtcNow - lastFriendsSync).TotalSeconds < 10)
            return;

        lastFriendsSync = DateTime.UtcNow;
        friendsSyncInFlight = true;
        var relayUrl = streamPanel?.RelayUrl ?? config.RelayUrl;
        var uid = xivAuth.GetPlayerUid();

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var response = await RelayRoundTripAsync(relayUrl,
                    new ControlMsg { Action = "friends_get", Uid = uid });
                if (response == null)
                    return; // network failure — try again on the next cooldown

                if (response.Type != "friends_list" || response.Friends == null)
                {
                    // Old relay ("Unknown action") or a rejected identity:
                    // keep the local roster and stop retrying this session.
                    log.Information("[Sync] friends list unavailable from relay: {Msg}",
                        response.Message ?? "no response");
                    friendsFetched = true;
                    return;
                }

                var server = response.Friends;

                HashSet<string> removals;
                lock (friendsLock)
                {
                    removals = new HashSet<string>(offlineFriendRemovals);
                }

                foreach (var key in removals)
                {
                    var removed = await RelayRoundTripAsync(relayUrl,
                        new ControlMsg { Action = "friend_remove", Uid = uid, FriendKey = key });
                    if (removed?.Type == "friends_list" && removed.Friends != null)
                        server = removed.Friends;
                }

                lock (friendsLock)
                {
                    offlineFriendRemovals.Clear();
                }

                var serverKeys = new HashSet<string>(
                    server.Select(f => StreamPanel.NormalizeId(f.Key)));
                var local = FriendsSnapshot();
                foreach (var friend in local)
                {
                    var key = StreamPanel.NormalizeId(friend.Key);
                    if (serverKeys.Contains(key))
                        continue;

                    var added = await RelayRoundTripAsync(relayUrl,
                        new ControlMsg
                        {
                            Action = "friend_add",
                            Uid = uid,
                            FriendKey = key,
                            FriendName = friend.Name,
                        });
                    if (added?.Type == "friends_list" && added.Friends != null)
                        server = added.Friends;
                }

                ApplyFriendsList(server);
                friendsFetched = true;
            }
            catch (Exception ex)
            {
                log.Verbose($"[Sync] friends sync failed: {ex.Message}");
            }
            finally
            {
                friendsSyncInFlight = false;
            }
        });
    }

    private void AddFriendEntry(string normalizedKey, string name)
    {
        lock (friendsLock)
        {
            syncFriends.Add(new SyncFriend { Key = normalizedKey, Name = name });
            config.SyncFriends = new List<SyncFriend>(syncFriends);
        }

        SaveConfig();
        lastSyncCheck = DateTime.MinValue;
        SyncFriendMutation(new ControlMsg
        {
            Action = "friend_add",
            Uid = xivAuth.GetPlayerUid(),
            FriendKey = normalizedKey,
            FriendName = name,
        });
    }

    private void RemoveFriendEntry(string normalizedKey)
    {
        lock (friendsLock)
        {
            syncFriends.RemoveAll(friend => StreamPanel.NormalizeId(friend.Key) == normalizedKey);
            config.SyncFriends = new List<SyncFriend>(syncFriends);
        }

        SaveConfig();
        SyncFriendMutation(new ControlMsg
        {
            Action = "friend_remove",
            Uid = xivAuth.GetPlayerUid(),
            FriendKey = normalizedKey,
        });
    }

    // Push one roster mutation to the relay and adopt its response. Offline,
    // the local edit stands and the next sync reconciles it (removals queue
    // explicitly; adds are just local-only entries the sync will upload).
    private void SyncFriendMutation(ControlMsg request)
    {
        if (!FriendsRelayAvailable)
        {
            if (request.Action == "friend_remove" && request.FriendKey != null)
            {
                lock (friendsLock)
                {
                    offlineFriendRemovals.Add(request.FriendKey);
                }
            }

            return;
        }

        var relayUrl = streamPanel?.RelayUrl ?? config.RelayUrl;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var response = await RelayRoundTripAsync(relayUrl, request);
                if (response?.Type == "friends_list" && response.Friends != null)
                    ApplyFriendsList(response.Friends);
            }
            catch (Exception ex)
            {
                log.Verbose($"[Sync] friend update failed: {ex.Message}");
            }
        });
    }

    // Poll the relay for which synced friends are live (every few seconds).
    private void PollSyncStatus()
    {
        var roster = FriendsSnapshot();
        if (streamPanel == null || roster.Count == 0)
            return;
        if (syncCheckInFlight)
            return;
        if ((DateTime.UtcNow - lastSyncCheck).TotalSeconds < 5)
            return;

        lastSyncCheck = DateTime.UtcNow;
        syncCheckInFlight = true;

        var keys = roster
            .Select(f => StreamPanel.NormalizeId(f.Key))
            .Where(k => k.Length >= 6)
            .Distinct()
            .ToList();
        var relayUrl = streamPanel.RelayUrl;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(new Uri(relayUrl.TrimEnd('/') + "/ws"), cts.Token);

                var json = new ControlMsg { Action = "sync_check", Keys = keys }.ToJson();
                await ws.SendAsync(
                    new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(json)),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

                var buffer = new byte[16384];
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    var msg = ControlMsg.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (msg?.Type == "sync_status" && msg.Live != null)
                    {
                        lock (liveStatus)
                        {
                            liveStatus.Clear();
                            foreach (var info in msg.Live)
                                liveStatus[StreamPanel.NormalizeId(info.PlayerId)] = info;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Verbose($"[Sync] presence check failed: {ex.Message}");
            }
            finally
            {
                syncCheckInFlight = false;
            }
        });
    }

    private WorldScreenRenderer GetOrCreateWatchScreen(string playerId)
    {
        if (watchScreens.TryGetValue(playerId, out var existing))
            return existing;

        var renderer = new WorldScreenRenderer(
            gameGui, textureProvider, streamConfig,
            GetPlayerPos, GetPlayerRot, GetNearbyPlayerPositions,
            savedPosition: null);
        watchScreens[playerId] = renderer;
        return renderer;
    }

    // Only one screen can be in placement mode at a time.
    private void BeginPlacement(WorldScreenRenderer target)
    {
        worldScreen!.PlacementMode = false;
        foreach (var r in watchScreens.Values)
            r.PlacementMode = false;
        target.PlacementMode = true;
    }

    private void DrawWorldScreenSection()
    {
        if (worldScreen == null) return;

        DrawInsetText("World screen");

        if (worldScreen.PlacementMode)
        {
            DrawInsetTextWrapped("Click a surface to place the screen's bottom edge there. It mounts to walls and stands upright on floors or object tops. Right-click to cancel.");
            if (ImGui.Button("Cancel placement", new Vector2(-1, 0)))
                worldScreen.PlacementMode = false;
        }
        else
        {
            if (ImGui.Button("Place local screen in world", new Vector2(-1, 0)))
                BeginPlacement(worldScreen);

            if (worldScreen.IsPlaced)
            {
                ImGui.SameLine();
                if (ImGui.Button("Reset position"))
                    worldScreen.ClearPlacement();
            }

            if (!string.IsNullOrEmpty(worldScreen.OcclusionDebug))
                ImGui.TextWrapped($"Occlusion: {worldScreen.OcclusionDebug}");
        }

        if (dxScreen is { Failed: true })
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "DX11 mode failed to initialise — falling back to the overlay.");

        if (watchScreens.Count > 0)
            ImGui.TextDisabled("Watched screens use the live host's placement and size.");
    }

    // Draw the viewer windows for watched streams (called from Plugin.Draw).
    public void DrawViewerWindow() => streamPanel?.DrawViewerWindows();

    // The main window just closed: end the session (stop live, stop watching,
    // close viewer windows — world screens disappear with them).
    public void OnMainWindowClosed()
    {
        streamPanel?.StopAll();
    }

    // Draw the world-placed screens (called from Plugin.Draw every frame).
    public void DrawWorldScreen()
    {
        if (worldScreen == null) return;

        SynchronizeLiveScreenState();

        // Always submit watched screen state through the DX path. Besides
        // preparing depth-rendered quads, this applies the host placement and
        // hides the default Stream Viewer window for remote viewers.
        if (config.UseDxWorldScreen)
        {
            dxScreen?.Enable();
            DrawDxWorldScreen();

            // Placement overlays still render through ImGui.
            if (worldScreen.PlacementMode)
                worldScreen.Draw();
            foreach (var renderer in watchScreens.Values)
                if (renderer.PlacementMode)
                    renderer.Draw();
            return;
        }

        dxScreen?.Disable();

        // Placement mode always previews. The local broadcast itself is
        // opt-in, even while the player is streaming.
        if (worldScreen.PlacementMode || (config.ShowLocalWorldScreen && streamPanel is { IsLive: true }))
        {
            if (core is { IsGameLoaded: true })
            {
                var localVer = core.FrameVersion;
                if (localVer != localScreenVersion && core.TryGetFrame(out var localRgba, out var lw, out var lh))
                {
                    localScreenVersion = localVer;
                    worldScreen.SetFrame(localRgba, lw, lh);
                }
            }

            worldScreen.Draw();
        }

        if (streamPanel == null) return;

        // Reconcile even with the deck closed (stopped watches drop here).
        streamPanel.FlushRemoveQueue();

        // One world screen per watched stream; feed each from its client.
        var active = new System.Collections.Generic.HashSet<string>();
        foreach (var client in streamPanel.Clients)
        {
            if (client.SubscribedPlayerId == null)
                continue;

            var key = StreamPanel.NormalizeId(client.SubscribedPlayerId);
            active.Add(key);

            var renderer = GetOrCreateWatchScreen(key);
            var stateVersion = watchScreenStateVersions.TryGetValue(key, out var currentStateVersion)
                ? currentStateVersion : -1L;
            if (client.TryGetWorldScreenState(ref stateVersion, out var state))
            {
                watchScreenStateVersions[key] = stateVersion;
                renderer.ApplyRemoteState(state);
                if (renderer.IsPlaced)
                    streamPanel.SetWindowVisible(key, false);
            }

            var version = watchVersions.TryGetValue(key, out var v) ? v : -1L;
            if (client.TryGetFrame(ref version, out var rgba, out var w, out var h))
            {
                watchVersions[key] = version;
                renderer.SetFrame(rgba, w, h);
            }
            renderer.Draw();
        }

        // Drop screens for streams we no longer watch (positions stay saved).
        foreach (var key in watchScreens.Keys.Where(k => !active.Contains(k)).ToList())
        {
            watchScreens[key].Dispose();
            watchScreens.Remove(key);
            watchVersions.Remove(key);
            watchScreenStateVersions.Remove(key);
            dxWatchVersions.Remove(key);
            dxWatchFrames.Remove(key);
        }
    }

    private long localScreenVersion = -1;

    // DX11 path: estimate the camera, gather quads + frames, hand them to
    // the renderer; the Present hook draws them with the scene depth.
    private unsafe void DrawDxWorldScreen()
    {
        if (dxScreen == null || dxScreen.Failed || streamPanel == null)
            return;

        Matrix4x4? vp = null;
        Vector3? cameraPos = null;
        var projectionCandidates = new System.Collections.Generic.List<(string Name, Matrix4x4 Matrix)>();
        var cameraManager = FfxivCameraManager.Instance();
        var camera = cameraManager != null ? cameraManager->CurrentCamera : null;
        if (camera != null && camera->RenderCamera != null)
        {
            // This is the same transform used by FFXIVClientStructs'
            // WorldToScreen helper, including the game's exact clip-Z and
            // reverse-Z projection. It keeps our vertices in the same depth
            // space as RenderTargetManager.DepthStencil.
            Matrix4x4 sceneView = camera->ViewMatrix;
            Matrix4x4 renderView = camera->RenderCamera->ViewMatrix;
            Matrix4x4 projection = camera->RenderCamera->ProjectionMatrix;
            Matrix4x4 projection2 = camera->RenderCamera->ProjectionMatrix2;
            var projectionHasDepth = MathF.Abs(projection.M13) + MathF.Abs(projection.M23)
                + MathF.Abs(projection.M33) + MathF.Abs(projection.M43) > 1e-7f;
            var projection2HasDepth = MathF.Abs(projection2.M13) + MathF.Abs(projection2.M23)
                + MathF.Abs(projection2.M33) + MathF.Abs(projection2.M43) > 1e-7f;
            var selectedProjection = projectionHasDepth ? projection : projection2;

            // FFXIV's exposed view matrix is a 3x4 camera transform with a
            // zero homogeneous-W row. That preserves projected X/Y/W but
            // drops the projection's depth numerator when matrices are
            // multiplied on the CPU. Rebuild clip Z from the camera's exact
            // projection mode: clipZ = depthA * clipW + depthB.
            var nearPlane = camera->RenderCamera->NearPlane;
            var farPlane = camera->RenderCamera->FarPlane;
            double depthA;
            double depthB;
            if (camera->RenderCamera->StandardZ)
            {
                depthA = camera->RenderCamera->FiniteFarPlane
                    ? farPlane / (farPlane - nearPlane)
                    : 1.0;
                depthB = camera->RenderCamera->FiniteFarPlane
                    ? -nearPlane * farPlane / (farPlane - nearPlane)
                    : -nearPlane;
            }
            else
            {
                depthA = camera->RenderCamera->FiniteFarPlane
                    ? -nearPlane / (farPlane - nearPlane)
                    : 0.0;
                depthB = camera->RenderCamera->FiniteFarPlane
                    ? nearPlane * farPlane / (farPlane - nearPlane)
                    : nearPlane;
            }
            projectionCandidates.Add(("render/projection",
                BuildDxViewProjection(renderView, projection, depthA, depthB)));
            projectionCandidates.Add(("scene/projection",
                BuildDxViewProjection(sceneView, projection, depthA, depthB)));
            projectionCandidates.Add(("render/projection2",
                BuildDxViewProjection(renderView, projection2, depthA, depthB)));
            projectionCandidates.Add(("scene/projection2",
                BuildDxViewProjection(sceneView, projection2, depthA, depthB)));
            vp = projectionCandidates[0].Matrix;
            cameraPos = camera->Position;

            if (!loggedDxCameraMatrices)
            {
                loggedDxCameraMatrices = true;
                log.Information($"[DxScreen] scene camera: standardZ={camera->RenderCamera->StandardZ}, "
                    + $"finiteFar={camera->RenderCamera->FiniteFarPlane}, near={camera->RenderCamera->NearPlane:G6}, "
                    + $"far={camera->RenderCamera->FarPlane:G6}, projectionDepth={projectionHasDepth}, "
                    + $"projection2Depth={projection2HasDepth}, selected={(projectionHasDepth ? "ProjectionMatrix" : "ProjectionMatrix2")}, "
                    + $"z1=({projection.M13:G6},{projection.M23:G6},{projection.M33:G6},{projection.M43:G6}), "
                    + $"z2=({projection2.M13:G6},{projection2.M23:G6},{projection2.M33:G6},{projection2.M43:G6}), "
                    + $"sceneViewW={sceneView.M44:G6}, renderViewW={renderView.M44:G6}, "
                    + $"depthCurve=({depthA:G6},{depthB:G6}), "
                    + $"vpZ=({vp.Value.M13:G6},{vp.Value.M23:G6},{vp.Value.M33:G6},{vp.Value.M43:G6}), "
                    + $"vpW=({vp.Value.M14:G6},{vp.Value.M24:G6},{vp.Value.M34:G6},{vp.Value.M44:G6})");
            }
        }

        var calib = new System.Collections.Generic.List<(Vector3, Vector2)>();

        var quads = new System.Collections.Generic.List<Rendering.DxWorldRenderer.ScreenQuad>();
        var frames = new System.Collections.Generic.Dictionary<string, (byte[], int, int)>();

        // The local screen is opt-in even while the player is live.
        if (config.ShowLocalWorldScreen && streamPanel.IsLive
            && worldScreen!.IsPlaced && core is { IsGameLoaded: true })
        {
            var localVer = core.FrameVersion;
            if (localVer != dxLocalVersion && core.TryGetFrame(out var rgba, out var w, out var h))
            {
                dxLocalVersion = localVer;
                dxLocalFrame = rgba;
                dxLocalW = w;
                dxLocalH = h;
            }

            if (dxLocalFrame != null)
            {
                quads.Add(CreateDxScreenQuad("local", worldScreen, cameraPos));
                frames["local"] = (dxLocalFrame, dxLocalW, dxLocalH);
            }
        }

        // Watched streams use the live host's saved screen state.
        streamPanel.FlushRemoveQueue();
        var activeWatchScreens = new System.Collections.Generic.HashSet<string>();
        foreach (var client in streamPanel.Clients)
        {
            if (client.SubscribedPlayerId == null)
                continue;
            var key = StreamPanel.NormalizeId(client.SubscribedPlayerId);
            activeWatchScreens.Add(key);
            var renderer = GetOrCreateWatchScreen(key);
            var stateVersion = watchScreenStateVersions.TryGetValue(key, out var currentStateVersion)
                ? currentStateVersion : -1L;
            if (client.TryGetWorldScreenState(ref stateVersion, out var state))
            {
                watchScreenStateVersions[key] = stateVersion;
                renderer.ApplyRemoteState(state);
                if (renderer.IsPlaced)
                    streamPanel.SetWindowVisible(key, false);
            }

            if (!renderer.IsPlaced)
                continue;

            var version = dxWatchVersions.TryGetValue(key, out var v) ? v : -1L;
            if (client.TryGetFrame(ref version, out var rgba, out var w, out var h))
            {
                dxWatchVersions[key] = version;
                dxWatchFrames[key] = (rgba, w, h);
            }

            if (dxWatchFrames.TryGetValue(key, out var frame))
            {
                quads.Add(CreateDxScreenQuad(key, renderer, cameraPos));
                frames[key] = frame;
            }
        }

        foreach (var key in watchScreens.Keys.Where(key => !activeWatchScreens.Contains(key)).ToList())
        {
            watchScreens[key].Dispose();
            watchScreens.Remove(key);
            watchVersions.Remove(key);
            watchScreenStateVersions.Remove(key);
            dxWatchVersions.Remove(key);
            dxWatchFrames.Remove(key);
        }

        string? projectionSelection = null;
        string? projectionErrors = null;
        if (quads.Count > 0 && projectionCandidates.Count > 0)
        {
            // RenderCamera is synchronized to the retained scene-depth frame.
            // WorldToScreen can advance one game tick ahead; fitting to it
            // made a nearly coplanar screen flicker while the camera moved.
            vp = projectionCandidates[0].Matrix;
            _ = SelectBestDxProjection(
                quads[0], projectionCandidates, out _, out projectionErrors);
            projectionSelection = "render/projection (depth-synced)";
        }

        if (vp.HasValue && quads.Count > 0
            && (DateTime.UtcNow - lastDxProjectionComparison).TotalSeconds >= 3)
        {
            lastDxProjectionComparison = DateTime.UtcNow;
            LogDxProjectionComparison(quads[0], vp.Value, projectionSelection, projectionErrors);
        }

        dxScreen.SubmitFrame(vp, quads, frames, calib);
    }

    private static Matrix4x4 BuildDxViewProjection(
        Matrix4x4 view, Matrix4x4 projection, double depthA, double depthB)
    {
        var combined = view * projection;
        combined.M13 = (float)(depthA * combined.M14);
        combined.M23 = (float)(depthA * combined.M24);
        combined.M33 = (float)(depthA * combined.M34);
        combined.M43 = (float)(depthA * combined.M44 + depthB);
        return combined;
    }

    private Matrix4x4 SelectBestDxProjection(
        Rendering.DxWorldRenderer.ScreenQuad quad,
        System.Collections.Generic.IReadOnlyList<(string Name, Matrix4x4 Matrix)> candidates,
        out string selectedName,
        out string errorSummary)
    {
        var corners = GetDxQuadCorners(quad);
        var gamePixels = new Vector2[corners.Length];
        var validGameProjection = true;
        for (var i = 0; i < corners.Length; i++)
            validGameProjection &= gameGui.WorldToScreen(corners[i], out gamePixels[i], out _);

        var display = ImGui.GetIO().DisplaySize;
        var bestIndex = 0;
        var bestError = float.PositiveInfinity;
        var errors = new string[candidates.Count];
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var error = validGameProjection
                ? GetDxProjectionError(corners, gamePixels, candidates[candidateIndex].Matrix, display)
                : float.PositiveInfinity;
            errors[candidateIndex] = $"{candidates[candidateIndex].Name}={error:F2}px";
            if (error < bestError)
            {
                bestError = error;
                bestIndex = candidateIndex;
            }
        }

        selectedName = candidates[bestIndex].Name;
        errorSummary = string.Join(", ", errors);
        return candidates[bestIndex].Matrix;
    }

    private static float GetDxProjectionError(
        Vector3[] corners, Vector2[] gamePixels, Matrix4x4 candidate, Vector2 display)
    {
        var total = 0f;
        for (var i = 0; i < corners.Length; i++)
        {
            var clip = Vector4.Transform(new Vector4(corners[i], 1f), candidate);
            if (clip.W <= 1e-6f)
                return float.PositiveInfinity;
            var matrixPixel = new Vector2(
                (clip.X / clip.W * 0.5f + 0.5f) * display.X,
                (0.5f - clip.Y / clip.W * 0.5f) * display.Y);
            total += Vector2.Distance(matrixPixel, gamePixels[i]);
        }

        return total / corners.Length;
    }

    private static Vector3[] GetDxQuadCorners(Rendering.DxWorldRenderer.ScreenQuad quad)
    {
        var right = Vector3.Normalize(quad.Right) * quad.HalfWidth;
        var up = Vector3.Normalize(quad.Up) * quad.HalfHeight;
        return new[]
        {
            quad.Center - right + up,
            quad.Center + right + up,
            quad.Center + right - up,
            quad.Center - right - up,
        };
    }

    private void LogDxProjectionComparison(
        Rendering.DxWorldRenderer.ScreenQuad quad, Matrix4x4 vp,
        string? selection, string? errorSummary)
    {
        var corners = GetDxQuadCorners(quad);

        var display = ImGui.GetIO().DisplaySize;
        var labels = new[] { "tl", "tr", "br", "bl" };
        var parts = new string[corners.Length];
        for (var i = 0; i < corners.Length; i++)
        {
            var clip = Vector4.Transform(new Vector4(corners[i], 1f), vp);
            var matrixPixel = MathF.Abs(clip.W) > 1e-6f
                ? new Vector2(
                    (clip.X / clip.W * 0.5f + 0.5f) * display.X,
                    (0.5f - clip.Y / clip.W * 0.5f) * display.Y)
                : new Vector2(float.NaN, float.NaN);
            var inFront = gameGui.WorldToScreen(corners[i], out var gamePixel, out var inView);
            var delta = Vector2.Distance(matrixPixel, gamePixel);
            parts[i] = $"{labels[i]} game=({gamePixel.X:F1},{gamePixel.Y:F1}) "
                + $"vp=({matrixPixel.X:F1},{matrixPixel.Y:F1}) d={delta:F2} "
                + $"front={inFront} view={inView}";
        }

        log.Information($"[DxScreen] projection comparison: selected={selection}; {errorSummary}; "
            + string.Join("; ", parts));
    }

    private Rendering.DxWorldRenderer.ScreenQuad CreateDxScreenQuad(
        string id, WorldScreenRenderer renderer, Vector3? cameraPos)
    {
        var center = renderer.ScreenPosition;
        // Placement already stores a small 0.03-yalm surface offset. Give
        // the depth-tested quad another render-only 0.05-yalm clearance so
        // rough/curved support geometry cannot z-fight through a flat video
        // plane as the camera moves. This does not alter the saved anchor.
        if (renderer.SurfaceNormal is { } surfaceNormal
            && surfaceNormal.LengthSquared() > 1e-8f)
        {
            center += Vector3.Normalize(surfaceNormal) * 0.05f;
        }
        renderer.GetQuadBasis(cameraPos.GetValueOrDefault(center + Vector3.UnitZ), out var right, out var up);

        return new Rendering.DxWorldRenderer.ScreenQuad
        {
            Id = id,
            Center = center,
            Right = right,
            Up = up,
            HalfWidth = renderer.ScreenWidth / 2f,
            // Screens keep their own aspect (3:2 classic, or the core's
            // declared one); watched streams additionally supply a
            // host-authoritative width.
            HalfHeight = renderer.ScreenWidth / (2f * renderer.Aspect),
        };
    }

    internal WorldScreenRenderer? WorldScreen => worldScreen;

    public void Dispose()
    {
        AutoSave();
        framework.Update -= OnFrameworkUpdate;
        xivAuth.Dispose();
        streamPanel?.Dispose();
        streamPanel = null;
        presence?.Dispose();
        presence = null;
        dxScreen?.Dispose();
        netplayPanel?.Dispose();
        netplayPanel = null;
        worldScreen?.Dispose();
        worldScreen = null;
        foreach (var renderer in watchScreens.Values)
            renderer.Dispose();
        watchScreens.Clear();
        StopAudio();
        texture?.Dispose();
        texture = null;
        core?.Dispose();
        core = null;
        hwRender?.Dispose();
        hwRender = null;
    }
}
