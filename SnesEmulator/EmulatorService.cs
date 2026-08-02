using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using SnesEmulator.Emulation;
using SnesEmulator.Streaming;
using EmulatorStream;

namespace SnesEmulator;

// Owns the emulation core and its presentation: a single window styled as one piano-black retro TV
// unit - a CRT screen with a control strip, plus a recessed control deck (ROM / Keyboard /
// Controller / Settings tabs) docked to the right. The deck collapses away via a button in the TV's
// bottom-right corner, leaving just the TV. The core runs on its own thread (see RetroCore); this
// class only displays the latest frame and handles input suppression on the game thread.
public sealed class EmulatorService : IDisposable
{
    private const int MaxScale = 5;
    private const float Bezel = 26f;
    private const float ControlStrip = 48f;
    private const float PanelGap = 10f;
    private const float PanelWidth = 360f;
    private const int PanelThemeColorCount = 23;

    private const uint Black = 0xFF000000;

    // Solution Nine palette — translucent shiny black body, electric neon accents.
    private const uint ShellBody = 0xA6080808;       // ~65% opaque black
    private const uint ShellHighlight = 0x80141414;  // ~50% lifted black
    private const uint Sheen = 0x602A2A2A;           // subtle sheen
    private const uint GlossEdge = 0xA01A1A1A;       // dark edge
    private const uint GlossFill = 0xA6040404;       // near-black
    private const uint NeonCyan = 0xFFFFE500;        // #00E5FF — electric cyan
    private const uint NeonPink = 0xFF8040FF;        // #FF4080 — hot magenta-pink
    private const uint NeonAmber = 0xFF30B8FF;       // #FFB830 — warm amber-gold
    private const uint NeonViolet = 0xFFFF40AA;      // #AA40FF — violet-purple
    private const uint DeckBody = 0xA6060606;        // translucent shiny black panel
    private const uint TextDim = 0xFF9A8AAA;         // cool lavender-gray
    private const uint TextBright = 0xFFE0D8F0;      // ice-silver
    private const uint LedOn = 0xFFFFE500;           // cyan when on
    private const uint LedOff = 0xFF8040FF;          // dim pink when off

    private static readonly SnesButton[] ButtonOrder =
    {
        SnesButton.Up, SnesButton.Down, SnesButton.Left, SnesButton.Right,
        SnesButton.A, SnesButton.B, SnesButton.X, SnesButton.Y,
        SnesButton.L, SnesButton.R, SnesButton.Start, SnesButton.Select,
    };

    private static readonly SnesButton[] ControllerButtonOrder =
    {
        SnesButton.A, SnesButton.B, SnesButton.X, SnesButton.Y,
        SnesButton.L, SnesButton.R, SnesButton.Start, SnesButton.Select,
    };

    private static readonly ushort[] XInputButtonFlags =
    {
        GamepadReader.A, GamepadReader.B, GamepadReader.X, GamepadReader.Y,
        GamepadReader.LeftShoulder, GamepadReader.RightShoulder,
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
    private readonly CoreManager coreManager;
    private CoreInfo? selectedCore;
    private StreamPanel? streamPanel;
    private NetplayPanel? netplayPanel;
    private WorldScreenRenderer? worldScreen;

    private RetroCore? core;
    private AudioPlayer? audio;
    private IDalamudTextureWrap? texture;
    private int textureWidth;
    private int textureHeight;
    private long lastFrameVersion = -1;
    private bool coreLoadAttempted;
    private volatile bool focused;

    private bool panelOpen = true;
    private bool screenOn;

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

    public EmulatorService(Configuration config, IDalamudPluginInterface pluginInterface, ITextureProvider textureProvider, IPluginLog log, InputManager inputManager, IFramework framework, IGameGui gameGui, IObjectTable objectTable)
    {
        this.config = config;
        this.pluginInterface = pluginInterface;
        this.textureProvider = textureProvider;
        this.log = log;
        this.inputManager = inputManager;
        this.framework = framework;

        var pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        coreManager = new CoreManager(
            pluginDir,
            (msg, args) => log.Information(msg, args),
            (msg, args) => log.Error(msg, args));

        // Restore the previously selected core, or pick the default.
        selectedCore = !string.IsNullOrEmpty(config.SelectedCorePath)
            ? coreManager.FindByPath(config.SelectedCorePath)
            : null;
        selectedCore ??= coreManager.GetDefault();

        romBrowser = new RomBrowser(config, SelectRom, GetRomExtensions);
        streamPanel = new StreamPanel(
            config.GetStreamConfig(), textureProvider,
            msg => log.Information("[Stream] {Msg}", msg),
            () => core,
            () => pluginInterface.SavePluginConfig(config));
        netplayPanel = new NetplayPanel(
            config,
            msg => log.Information("[Netplay] {Msg}", msg),
            () => core,
            inputManager);
        worldScreen = new WorldScreenRenderer(
            gameGui, textureProvider, config.GetStreamConfig(),
            () => objectTable.LocalPlayer?.Position,
            () => objectTable.LocalPlayer?.Rotation ?? 0f,
            () =>
            {
                var positions = new System.Collections.Generic.List<Vector3>();
                var local = objectTable.LocalPlayer;
                foreach (var obj in objectTable)
                {
                    if (obj != local && obj.Position != Vector3.Zero)
                        positions.Add(obj.Position);
                }
                return positions;
            });

        framework.Update += OnFrameworkUpdate;
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

    private void OnFrameworkUpdate(IFramework fw)
    {
        // Poll gamepad on the UI thread (WinRT requirement).
        inputManager.PollGamepad();

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
        if (screenOn)
        {
            UpdateTexture();
        }

        if (core != null)
        {
            core.Paused = !screenOn;
        }

        var scale = Math.Max(1, config.ResolutionScale);
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

            // Solution Nine shell — warm charcoal with neon trim.
            var shellMin = origin;
            var shellMax = origin + new Vector2(windowW, windowH);
            drawList.AddRectFilled(shellMin, shellMax, ShellBody, 12f);
            drawList.AddRectFilled(shellMin, new Vector2(shellMax.X, shellMin.Y + 16), ShellHighlight, 12f);
            drawList.AddRectFilled(shellMin, new Vector2(shellMax.X, shellMin.Y + 8), ShellBody, 12f);
            drawList.AddRect(shellMin + new Vector2(1, 1), shellMax - new Vector2(1, 1), ShellHighlight, 11f, 0, 1f);

            // Neon turquoise outline — the signature Solution Nine glow.
            drawList.AddRect(shellMin, shellMax, NeonCyan, 12f, 0, 2f);

            // Corner accent stripes (coral + yellow), like street signage.
            const float stripe = 5f;
            drawList.AddLine(
                new Vector2(shellMin.X + 14, shellMin.Y + 2),
                new Vector2(shellMin.X + 14 + 40, shellMin.Y + 2),
                NeonPink, stripe);
            drawList.AddLine(
                new Vector2(shellMin.X + 60, shellMin.Y + 2),
                new Vector2(shellMin.X + 60 + 24, shellMin.Y + 2),
                NeonAmber, stripe);

            // Close button (top-right bezel) — magenta neon.
            var btnSize = new Vector2(18, 18);
            var btnPos = origin + new Vector2(windowW - Bezel - btnSize.X, (Bezel - btnSize.Y) / 2f);
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton("##close", btnSize))
            {
                show = false;
            }

            var btnHovered = ImGui.IsItemHovered();
            var btnCenter = btnPos + btnSize / 2;
            var x = 4f;
            var xColor = btnHovered ? NeonViolet : NeonPink;
            drawList.AddLine(btnCenter - new Vector2(x, x), btnCenter + new Vector2(x, x), xColor, 2f);
            drawList.AddLine(btnCenter + new Vector2(x, -x), btnCenter + new Vector2(-x, x), xColor, 2f);

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

        // Recessed piano-black deck.
        drawList.AddRectFilled(panelMin, panelMax, DeckBody, 8f);
        drawList.AddRect(panelMin + new Vector2(1, 1), panelMax - new Vector2(1, 1), ShellHighlight, 7f, 0, 1f);
        drawList.AddRect(panelMin, panelMax, Black, 8f, 0, 2f);

        ImGui.SetCursorScreenPos(panelMin + new Vector2(6, 6));
        PushPanelTheme();
        ImGui.BeginChild("##sidepanel", new Vector2(panelW - 12, panelH - 12), false);

        if (ImGui.BeginTabBar("##retro-tabs"))
        {
            if (ImGui.BeginTabItem("ROM"))
            {
                DrawRomTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Keyboard"))
            {
                DrawKeyboardTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Controller"))
            {
                DrawControllerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Online"))
            {
                streamPanel?.DrawTab();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                netplayPanel?.DrawTab();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawWorldScreenSection();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(PanelThemeColorCount);
    }

    private static void PushPanelTheme()
    {
        // Translucent shiny black surfaces with neon Solution Nine accents.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, 0x00000000);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0x80080808);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0xA0141414);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0xA01E1E1E);
        ImGui.PushStyleColor(ImGuiCol.Button, 0x800C0C0C);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xA01A1A1A);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xA0242424);
        ImGui.PushStyleColor(ImGuiCol.Header, 0x800C0C0C);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0xA01A1A1A);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0xA0242424);
        ImGui.PushStyleColor(ImGuiCol.Text, 0xFFE0D8F0);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, 0xFF7A6A8A);
        ImGui.PushStyleColor(ImGuiCol.Separator, 0x601A1A1A);
        ImGui.PushStyleColor(ImGuiCol.Tab, 0x80080808);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, 0xA01A1A1A);
        ImGui.PushStyleColor(ImGuiCol.TabActive, 0xA0141414);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 0x40060606);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, 0x801A1A1A);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, 0xA0242424);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, 0xA02E2E2E);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, 0xFFFFE500);   // cyan
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, 0xFFFFE500);   // cyan
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, 0xFF8040FF); // pink
    }

    private void DrawCoreSelector()
    {
        ImGui.TextUnformatted("Core");

        if (coreManager.Cores.Count == 0)
        {
            ImGui.TextWrapped(coreManager.ScanError);
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
            ImGui.TextDisabled($"Supports: {string.Join(" ", selectedCore.Extensions)}");
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

    private void DrawKeyboardTab()
    {
        ImGui.TextWrapped("Click a binding to rebind, right-click to reset.");
        ImGui.Separator();
        HandleKeyRebinding();

        foreach (var button in ButtonOrder)
        {
            var name = button.ToString();
            config.KeyBindings.TryGetValue(name, out var vk);

            ImGui.TextUnformatted(name);
            ImGui.SameLine(120);

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
        // Diagnostics — shows what the gamepad reader sees.
        var gp = inputManager.Gamepad;
        ImGui.TextUnformatted($"Controller connected: {gp.Connected}");
        ImGui.TextUnformatted($"Buttons: 0x{gp.Buttons:X4}  Stick: {gp.LeftStickX:F2}, {gp.LeftStickY:F2}");
        ImGui.TextUnformatted($"Input mode: {config.InputMode}");
        ImGui.TextWrapped($"Debug: {gp.DebugInfo}");
        ImGui.Separator();

        ImGui.TextWrapped("D-Pad is on the left stick. Click a button, then press a controller button to rebind. Right-click to reset.");
        ImGui.Separator();
        HandleControllerRebinding();

        foreach (var button in ControllerButtonOrder)
        {
            var name = button.ToString();
            config.ControllerBindings.TryGetValue(name, out var flag);

            ImGui.TextUnformatted(name);
            ImGui.SameLine(120);

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

    private void DrawSettingsTab()
    {
        ImGui.TextUnformatted("Options");

        var volume = (int)Math.Round(config.Volume * 100f);
        if (ImGui.SliderInt("Volume %", ref volume, 0, 100))
        {
            SetVolume(volume / 100f);
            SaveConfig();
        }

        if (ImGui.BeginCombo("Resolution scale", $"{config.ResolutionScale}x"))
        {
            for (var i = 1; i <= MaxScale; i++)
            {
                if (ImGui.Selectable($"{i}x", i == config.ResolutionScale))
                {
                    config.ResolutionScale = i;
                    SaveConfig();
                }
            }

            ImGui.EndCombo();
        }

        var showFps = config.ShowFps;
        if (ImGui.Checkbox("Show FPS overlay", ref showFps))
        {
            config.ShowFps = showFps;
            SaveConfig();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Input");
        ImGui.SetNextItemWidth(-1);
        var modeNames = new[] { "Both", "Keyboard only", "Controller only" };
        var currentMode = (int)config.InputMode;
        if (ImGui.Combo("##inputmode", ref currentMode, modeNames, modeNames.Length))
        {
            config.InputMode = (InputMode)currentMode;
            SaveConfig();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("CRT effects");

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
            var newTexture = textureProvider.CreateFromRaw(spec, rgba, "SnesEmulator.Frame");
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

            var newCore = new RetroCore
            {
                SystemDirectory = systemDir,
                SaveDirectory = saveDir,
                InputState = inputManager.GetInputState,
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

            var resolvedPath = ResolveRomPath(romPath);
            if (core.LoadGame(resolvedPath))
            {
                screenOn = true;
                crtAnimTarget = 1f;
                StopAudio();

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
                status = "The core refused to load this ROM.";
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
            return ExtractRomFromZip(path);
        }

        return path;
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

        if (entry == null && archive.Entries.Count == 1)
        {
            entry = archive.Entries[0];
        }

        if (entry == null)
        {
            throw new InvalidOperationException("No compatible ROM found inside the archive.");
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
        config.Volume = volume;
        audio?.SetVolume(volume);
    }

    private void SaveConfig() => pluginInterface.SavePluginConfig(config);

    private void StopAudio()
    {
        audio?.Dispose();
        audio = null;
    }

    private void DrawWorldScreenSection()
    {
        if (worldScreen == null) return;

        ImGui.TextUnformatted("World screen");

        var width = config.ScreenWidth;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##screenwidth", ref width, 0.5f, 5f, "%.1f yalms wide"))
        {
            config.ScreenWidth = width;
            SaveConfig();
        }

        var height = config.ScreenHeight;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##screenheight", ref height, 0f, 4f, "%.1f yalms high"))
        {
            config.ScreenHeight = height;
            SaveConfig();
        }

        var opacity = config.ScreenOpacity;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##screenopacity", ref opacity, 0.1f, 1f, "%.0f%% opacity"))
        {
            config.ScreenOpacity = opacity;
            SaveConfig();
        }

        if (worldScreen.PlacementMode)
        {
            ImGui.TextWrapped("Click in the world to place the screen. Right-click to cancel.");
            if (ImGui.Button("Cancel placement", new Vector2(-1, 0)))
                worldScreen.PlacementMode = false;
        }
        else
        {
            if (ImGui.Button("Place screen in world", new Vector2(-1, 0)))
                worldScreen.PlacementMode = true;

            if (worldScreen.IsPlaced)
            {
                ImGui.SameLine();
                if (ImGui.Button("Reset position"))
                {
                    config.ScreenPosition = null;
                    SaveConfig();
                }
            }

            if (!string.IsNullOrEmpty(worldScreen.OcclusionDebug))
                ImGui.TextWrapped($"Occlusion: {worldScreen.OcclusionDebug}");
        }
    }

    // Draw the spectator viewer window (called from Plugin.Draw alongside the main window).
    public void DrawViewerWindow() => streamPanel?.DrawViewerWindow();

    // Draw the world-placed screen (called from Plugin.Draw every frame).
    public void DrawWorldScreen()
    {
        if (worldScreen == null) return;

        // Spectator frames take priority; fall back to local core frames.
        var client = streamPanel?.GetStreamClient();
        var gotFrame = false;
        if (client != null)
        {
            var version = worldScreenVersion;
            if (client.TryGetFrame(ref version, out var rgba, out var w, out var h))
            {
                worldScreenVersion = version;
                worldScreen.SetFrame(rgba, w, h);
                gotFrame = true;
            }
        }

        // Host / local play: feed the core's own framebuffer.
        if (!gotFrame && core is { IsGameLoaded: true })
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

    private long worldScreenVersion;
    private long localScreenVersion = -1;

    internal WorldScreenRenderer? WorldScreen => worldScreen;

    public void Dispose()
    {
        AutoSave();
        framework.Update -= OnFrameworkUpdate;
        streamPanel?.Dispose();
        streamPanel = null;
        netplayPanel?.Dispose();
        netplayPanel = null;
        worldScreen?.Dispose();
        worldScreen = null;
        StopAudio();
        texture?.Dispose();
        texture = null;
        core?.Dispose();
        core = null;
    }
}
