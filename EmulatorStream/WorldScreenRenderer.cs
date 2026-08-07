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
    private const float SurfaceOffset = 0.03f;
    private const float SurfaceSamplePixels = 5f;
    private const float MaxSurfaceSampleDistance = 2f;

    // The product's classic TV look. Cores that declare their own display
    // aspect (PS1's 4:3) override it per screen via Aspect.
    public const float DefaultAspect = 3f / 2f;

    private readonly IGameGui gameGui;
    private readonly ITextureProvider textureProvider;
    private readonly StreamConfig config;
    private readonly Func<Vector3?> getPlayerPos;
    private readonly Func<float> getPlayerRot;
    private readonly Func<List<Vector3>> getNearbyPlayerPositions;
    private readonly Action<float[]>? persistPosition;

    public bool PlacementMode { get; set; }
    public Vector3 ScreenPosition { get; set; }
    public Vector3? SurfaceNormal { get; private set; }
    public bool IsPlaced { get; private set; }
    public string OcclusionDebug { get; private set; } = "";
    private float? remoteScreenWidth;

    // Local screens follow the current local configuration. Watched screens
    // receive an explicit width from their live host instead.
    public float ScreenWidth => remoteScreenWidth ?? config.ScreenWidth;

    // Display aspect (width / height) of the video on this screen.
    public float Aspect { get; set; } = DefaultAspect;

    private float HalfHeight => ScreenWidth / (2f * Aspect);

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

        if (savedPosition is { Length: >= 3 })
        {
            ScreenPosition = new Vector3(savedPosition[0], savedPosition[1], savedPosition[2]);
            if (savedPosition.Length >= 6)
            {
                var savedNormal = new Vector3(savedPosition[3], savedPosition[4], savedPosition[5]);
                if (savedNormal.LengthSquared() > 1e-8f)
                    SurfaceNormal = Vector3.Normalize(savedNormal);
            }
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
                var tex = textureProvider.CreateFromRaw(spec, pendingFrame, "RetroXIV.WorldScreen");
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
        var halfHeight = HalfHeight;
        var previewPos = playerPos.Value + forward * 3f
            + new Vector3(0, config.ScreenHeight + halfHeight, 0);

        var mouse = ImGui.GetIO().MousePos;
        Vector3? previewNormal = null;
        if (TryGetSurfacePlacement(mouse, camPos: playerPos.Value + new Vector3(0, 1.5f, 0),
                out var surfacePosition, out var surfaceNormal))
        {
            previewPos = surfacePosition;
            previewNormal = surfaceNormal;
        }

        // Draw the preview quad in the world.
        DrawWorldQuad(previewPos, 0x80FFFFFF, previewNormal);

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
            var text = "Click surface = place bottom edge here    Right-click = cancel";
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
                SurfaceNormal = previewNormal;
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

    private void DrawWorldQuad(Vector3 position, uint tint = 0xFFFFFFFF, Vector3? normalOverride = null)
    {
        var playerPos = getPlayerPos();
        if (playerPos == null) return;

        var camPos = playerPos.Value + new Vector3(0, 1.5f, 0);

        // NOTE: True depth occlusion requires DX11 pipeline hooking (Phase 5).
        // ImGui overlays always render after the game's entire frame.
        // Opacity slider is the practical workaround for now.

        var orientation = normalOverride ?? SurfaceNormal;
        if (!PlacementMode && orientation == null)
            orientation = EnsureFixedOrientation(position, camPos);

        var (tl, tr, br, bl) = ComputeQuadCorners(position, camPos, orientation);
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

    private (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) ComputeQuadCorners(
        Vector3 center, Vector3 camPos, Vector3? surfaceNormal)
    {
        var halfW = ScreenWidth / 2f;
        var halfH = halfW / Aspect;

        GetQuadBasis(center, camPos, surfaceNormal, out var right, out var up);

        return (
            center - right * halfW + up * halfH,
            center + right * halfW + up * halfH,
            center + right * halfW - up * halfH,
            center - right * halfW - up * halfH);
    }

    // Applies host-authoritative state for a watched stream. This deliberately
    // never invokes persistPosition: viewers must not overwrite the host's
    // placement with their own local settings.
    public void ApplyRemoteState(WorldScreenState? state)
    {
        if (state?.Width > 0f)
            remoteScreenWidth = MathF.Max(0.5f, MathF.Min(20f, state.Width));

        // Host-authoritative display aspect; hosts/relays from before the
        // aspect field fall back to the classic 3:2.
        Aspect = state is { Aspect: > 0f }
            ? MathF.Max(0.4f, MathF.Min(4f, state.Aspect))
            : DefaultAspect;

        if (state?.Position is not { Length: >= 3 })
        {
            IsPlaced = false;
            SurfaceNormal = null;
            PlacementMode = false;
            return;
        }

        var position = state.Position;
        ScreenPosition = new Vector3(position[0], position[1], position[2]);
        SurfaceNormal = null;
        if (position.Length >= 6)
        {
            var normal = new Vector3(position[3], position[4], position[5]);
            if (normal.LengthSquared() > 1e-8f)
                SurfaceNormal = Vector3.Normalize(normal);
        }
        IsPlaced = true;
        PlacementMode = false;
    }

    // Surface-mounted placements keep this basis fixed. Legacy three-value
    // placements are migrated once to an upright fixed direction instead of
    // continuing to face the moving camera every frame.
    public void GetQuadBasis(Vector3 cameraPosition, out Vector3 right, out Vector3 up)
    {
        var orientation = SurfaceNormal ?? EnsureFixedOrientation(ScreenPosition, cameraPosition);
        GetQuadBasis(ScreenPosition, cameraPosition, orientation, out right, out up);
    }

    private Vector3 EnsureFixedOrientation(Vector3 center, Vector3 cameraPosition)
    {
        if (SurfaceNormal is { } existing)
            return existing;

        var orientation = cameraPosition - center;
        orientation.Y = 0f;
        if (orientation.LengthSquared() < 1e-8f)
            orientation = Vector3.UnitZ;
        orientation = Vector3.Normalize(orientation);

        SurfaceNormal = orientation;
        SavePosition();
        return orientation;
    }

    private static void GetQuadBasis(
        Vector3 center, Vector3 cameraPosition, Vector3? surfaceNormal,
        out Vector3 right, out Vector3 up)
    {
        var forward = surfaceNormal ?? (cameraPosition - center);
        if (forward.LengthSquared() < 1e-8f)
            forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);

        // Keep the picture upright on walls and sloped objects. For a nearly
        // horizontal surface, derive a stable right vector from the camera.
        right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared() < 1e-8f)
        {
            var view = cameraPosition - center;
            if (view.LengthSquared() < 1e-8f)
                view = Vector3.UnitZ;
            right = Vector3.Cross(forward, view);
        }
        if (right.LengthSquared() < 1e-8f)
            right = Vector3.Cross(Vector3.UnitZ, forward);
        if (right.LengthSquared() < 1e-8f)
            right = Vector3.UnitX;

        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(forward, right));
    }

    private bool TryGetSurfacePlacement(
        Vector2 mouse, Vector3 camPos, out Vector3 position, out Vector3 normal)
    {
        position = default;
        normal = default;
        if (!gameGui.ScreenToWorld(mouse, out var hit, 60f))
            return false;

        var leftOk = gameGui.ScreenToWorld(mouse - new Vector2(SurfaceSamplePixels, 0), out var left, 60f);
        var rightOk = gameGui.ScreenToWorld(mouse + new Vector2(SurfaceSamplePixels, 0), out var right, 60f);
        var upOk = gameGui.ScreenToWorld(mouse - new Vector2(0, SurfaceSamplePixels), out var top, 60f);
        var downOk = gameGui.ScreenToWorld(mouse + new Vector2(0, SurfaceSamplePixels), out var bottom, 60f);

        var hasTangentX = TryBuildSurfaceTangent(hit, leftOk, left, rightOk, right, out var tangentX);
        var hasTangentY = TryBuildSurfaceTangent(hit, upOk, top, downOk, bottom, out var tangentY);

        var supportNormal = Vector3.UnitY;
        if (hasTangentX && hasTangentY)
        {
            var sampledNormal = Vector3.Cross(tangentX, tangentY);
            if (sampledNormal.LengthSquared() > 1e-8f)
            {
                supportNormal = Vector3.Normalize(sampledNormal);
                if (Vector3.Dot(supportNormal, camPos - hit) < 0f)
                    supportNormal = -supportNormal;
            }
        }

        // Walls receive a flush-mounted screen. Floors and object tops use
        // the hit as a support point and stand the screen upright, facing the
        // player horizontally instead of laying the video flat.
        normal = supportNormal;
        if (MathF.Abs(supportNormal.Y) >= 0.65f)
        {
            normal = camPos - hit;
            normal.Y = 0f;
            if (normal.LengthSquared() < 1e-8f)
                normal = Vector3.UnitZ;
            normal = Vector3.Normalize(normal);
        }

        GetQuadBasis(hit, camPos, normal, out var quadRight, out var quadUp);
        var halfWidth = ScreenWidth / 2f;
        var halfHeight = HalfHeight;

        // The click is the bottom-center support point, not the center of the
        // picture. This prevents half of a large screen entering the ground.
        position = hit + supportNormal * SurfaceOffset + quadUp * halfHeight;

        // Sloped geometry can give the right axis a vertical component. Move
        // the whole quad up enough that neither bottom corner crosses below
        // the clicked surface height.
        var bottomCenter = position - quadUp * halfHeight;
        var bottomLeftY = (bottomCenter - quadRight * halfWidth).Y;
        var bottomRightY = (bottomCenter + quadRight * halfWidth).Y;
        var lowestBottomY = MathF.Min(bottomLeftY, bottomRightY);
        if (lowestBottomY < hit.Y)
            position += Vector3.UnitY * (hit.Y - lowestBottomY);

        return true;
    }

    private static bool TryBuildSurfaceTangent(
        Vector3 center,
        bool negativeOk, Vector3 negative,
        bool positiveOk, Vector3 positive,
        out Vector3 tangent)
    {
        tangent = default;
        var negativeLocal = negativeOk && Vector3.Distance(center, negative) <= MaxSurfaceSampleDistance;
        var positiveLocal = positiveOk && Vector3.Distance(center, positive) <= MaxSurfaceSampleDistance;

        if (negativeLocal && positiveLocal)
            tangent = positive - negative;
        else if (positiveLocal)
            tangent = positive - center;
        else if (negativeLocal)
            tangent = center - negative;

        return tangent.LengthSquared() > 1e-8f;
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
        SurfaceNormal = null;
        persistPosition?.Invoke(Array.Empty<float>());
    }

    private void SavePosition()
    {
        if (SurfaceNormal is { } normal)
        {
            persistPosition?.Invoke(new[]
            {
                ScreenPosition.X, ScreenPosition.Y, ScreenPosition.Z,
                normal.X, normal.Y, normal.Z,
            });
            return;
        }

        persistPosition?.Invoke(new[] { ScreenPosition.X, ScreenPosition.Y, ScreenPosition.Z });
    }

    public void Dispose()
    {
        screenTexture?.Dispose();
        screenTexture = null;
    }
}
