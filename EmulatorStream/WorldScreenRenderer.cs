using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace EmulatorStream;

// Renders a video stream as a screen placed in the game world.
// Placement: the screen appears 3 yalms in front of the player at eye height.
// Walk to where you want it, then click to confirm.
// One instance per screen (local play or one watched stream each); positions
// are owned by the caller via the savedPosition/persistPosition pair.
public sealed class WorldScreenRenderer
{
    private readonly IGameGui gameGui;
    private readonly ITextureProvider textureProvider;
    private readonly StreamConfig config;
    private readonly Func<Vector3?> getPlayerPos;
    private readonly Func<float> getPlayerRot;
    private readonly Func<List<Vector3>> getNearbyPlayerPositions;
    private readonly Action<float[]>? persistPosition;

    public bool PlacementMode { get; set; }
    public Vector3 ScreenPosition { get; set; }
    public bool IsPlaced { get; private set; }
    public string OcclusionDebug { get; private set; } = "";

    private IDalamudTextureWrap? screenTexture;
    private byte[]? pendingFrame;
    private int pendingW, pendingH;
    private readonly object frameLock = new();

    public WorldScreenRenderer(
        IGameGui gameGui,
        ITextureProvider textureProvider,
        StreamConfig config,
        Func<Vector3?> getPlayerPos,
        Func<float> getPlayerRot,
        Func<List<Vector3>> getNearbyPlayerPositions,
        float[]? savedPosition = null,
        Action<float[]>? persistPosition = null)
    {
        this.gameGui = gameGui;
        this.textureProvider = textureProvider;
        this.config = config;
        this.getPlayerPos = getPlayerPos;
        this.getPlayerRot = getPlayerRot;
        this.getNearbyPlayerPositions = getNearbyPlayerPositions;
        this.persistPosition = persistPosition;

        if (savedPosition is { Length: 3 })
        {
            ScreenPosition = new Vector3(savedPosition[0], savedPosition[1], savedPosition[2]);
            IsPlaced = true;
        }
    }

    public void SetFrame(byte[] rgba, int w, int h)
    {
        lock (frameLock)
        {
            pendingFrame = rgba;
            pendingW = w;
            pendingH = h;
        }
    }

    private void UploadTexture()
    {
        lock (frameLock)
        {
            if (pendingFrame == null) return;
            try
            {
                var spec = RawImageSpecification.Rgba32(pendingW, pendingH);
                var tex = textureProvider.CreateFromRaw(spec, pendingFrame, "SnesEmulator.WorldScreen");
                screenTexture?.Dispose();
                screenTexture = tex;
            }
            catch { }
            pendingFrame = null;
        }
    }

    public void Draw()
    {
        UploadTexture();

        if (PlacementMode)
        {
            DrawPlacementOverlay();
            return;
        }

        if (!IsPlaced || screenTexture == null)
            return;

        DrawWorldQuad(ScreenPosition);
    }

    // ── Placement mode ──────────────────────────────────────────────

    private void DrawPlacementOverlay()
    {
        var playerPos = getPlayerPos();
        if (playerPos == null) return;

        // Preview follows the mouse raycast into the world; fall back to
        // 3 yalms in front of the player when the ray hits nothing.
        var yaw = getPlayerRot();
        var forward = new Vector3((float)Math.Sin(yaw), 0, (float)Math.Cos(yaw));
        var previewPos = playerPos.Value + forward * 3f + new Vector3(0, config.ScreenHeight, 0);

        var mouse = ImGui.GetIO().MousePos;
        if (gameGui.ScreenToWorld(mouse, out var hit, 60f))
            previewPos = hit;

        // Draw the preview quad in the world.
        DrawWorldQuad(previewPos, 0x80FFFFFF);

        // Fullscreen transparent overlay to capture clicks.
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(io.DisplaySize);
        ImGui.SetNextWindowFocus();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0x01000000); // barely visible so ImGui processes it

        if (ImGui.Begin("##worldscreen_place", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings))
        {
            var drawList = ImGui.GetWindowDrawList();

            // Instructions at top-center.
            var text = "Click = place screen here    Right-click = cancel";
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(new Vector2((io.DisplaySize.X - textSize.X) / 2f, 40), 0xFFFFFFFF, text);

            // Crosshair at center.
            var cx = io.DisplaySize.X / 2f;
            var cy = io.DisplaySize.Y / 2f;
            drawList.AddLine(new Vector2(cx - 12, cy), new Vector2(cx + 12, cy), 0x80FFFFFF, 1.5f);
            drawList.AddLine(new Vector2(cx, cy - 12), new Vector2(cx, cy + 12), 0x80FFFFFF, 1.5f);

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ScreenPosition = previewPos;
                IsPlaced = true;
                PlacementMode = false;
                SavePosition();
            }
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                PlacementMode = false;
            }
        }

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    // ── World quad rendering ────────────────────────────────────────

    private void DrawWorldQuad(Vector3 position, uint tint = 0xFFFFFFFF)
    {
        var playerPos = getPlayerPos();
        if (playerPos == null) return;

        var camPos = playerPos.Value + new Vector3(0, 1.5f, 0);

        // NOTE: True depth occlusion requires DX11 pipeline hooking (Phase 5).
        // ImGui overlays always render after the game's entire frame.
        // Opacity slider is the practical workaround for now.

        var (tl, tr, br, bl) = ComputeQuadCorners(position, camPos);
        if (!ProjectQuad(tl, tr, br, bl, out var stl, out var str, out var sbr, out var sbl))
            return;
        if (float.IsNaN(stl.X) || float.IsNaN(str.X))
            return;

        var drawList = ImGui.GetBackgroundDrawList();

        if (screenTexture != null)
        {
            var alpha = (uint)(config.ScreenOpacity * 255) << 24;
            var colorTint = alpha | 0x00FFFFFF;
            drawList.AddImageQuad(screenTexture.Handle, stl, str, sbr, sbl,
                Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, colorTint);
        }
        else
        {
            drawList.AddQuadFilled(stl, str, sbr, sbl, 0x3000FF00);
        }

        drawList.AddQuad(stl, str, sbr, sbl, 0x60FFFFFF, 1f);
    }

    // ── Geometry helpers ────────────────────────────────────────────

    private (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) ComputeQuadCorners(Vector3 center, Vector3 camPos)
    {
        var halfW = config.ScreenWidth / 2f;
        var halfH = halfW * (2f / 3f); // 3:2 screen

        var forward = Vector3.Normalize(camPos - center);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);

        return (
            center - right * halfW + up * halfH,
            center + right * halfW + up * halfH,
            center + right * halfW - up * halfH,
            center - right * halfW - up * halfH);
    }

    private bool ProjectQuad(Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl,
        out Vector2 stl, out Vector2 str, out Vector2 sbr, out Vector2 sbl)
    {
        stl = str = sbr = sbl = Vector2.Zero;
        return gameGui.WorldToScreen(tl, out stl)
            && gameGui.WorldToScreen(tr, out str)
            && gameGui.WorldToScreen(br, out sbr)
            && gameGui.WorldToScreen(bl, out sbl);
    }

    public void ClearPlacement()
    {
        IsPlaced = false;
        persistPosition?.Invoke(Array.Empty<float>());
    }

    private void SavePosition()
    {
        persistPosition?.Invoke(new[] { ScreenPosition.X, ScreenPosition.Y, ScreenPosition.Z });
    }

    public void Dispose()
    {
        screenTexture?.Dispose();
        screenTexture = null;
    }
}
