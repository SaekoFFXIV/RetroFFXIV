using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace EmulatorStream;

// ImGui UI for hosting and spectating streams.  Draws the "Stream" tab
// contents inside the emulator's control deck, and a separate spectator
// viewer window when watching someone else's stream.
public sealed class StreamPanel : IDisposable
{
    private readonly StreamConfig config;
    private readonly ITextureProvider textureProvider;
    private readonly Action<string> log;
    private readonly Func<IEmulatorBackend?> getBackend;
    private readonly Action saveConfig;

    private StreamHost? host;
    private StreamClient? client;

    private string joinCode = string.Empty;
    private string relayUrl = string.Empty;
    private bool useWorldScreen;

    // Spectator viewer texture.
    private IDalamudTextureWrap? viewerTexture;
    private long lastViewerVersion = -1;
    private bool showViewerWindow;

    public bool IsHosting => host?.IsHosting == true;
    public bool IsLive => host?.IsLive == true;
    public bool IsSpectating => client?.IsConnected == true;
    public bool IsSubscribed => client?.SubscribedUid != null;
    public bool UseWorldScreen => useWorldScreen;
    public StreamClient? GetStreamClient() => client;
    public StreamHost? GetStreamHost() => host;
    public bool ShowViewerWindow
    {
        get => showViewerWindow;
        set => showViewerWindow = value;
    }

    public StreamPanel(
        StreamConfig config,
        ITextureProvider textureProvider,
        Action<string> log,
        Func<IEmulatorBackend?> getBackend,
        Action saveConfig)
    {
        this.config = config;
        this.textureProvider = textureProvider;
        this.log = log;
        this.getBackend = getBackend;
        this.saveConfig = saveConfig;
        relayUrl = config.RelayUrl;
    }

    // Draw the "Stream" tab contents inside the control deck.
    public void DrawTab()
    {
        DrawHostSection();
        ImGui.Separator();
        DrawSpectateSection();
        ImGui.Separator();
        DrawRelaySetting();
    }

    private void DrawHostSection()
    {
        ImGui.TextUnformatted("Host a stream");

        var backend = getBackend();
        if (backend is not { IsGameLoaded: true })
        {
            ImGui.TextWrapped("Load a game first, then start hosting.");
            return;
        }

        if (host is { IsHosting: true })
        {
            ImGui.TextWrapped($"Room code: {host.RoomCode}");
            ImGui.TextWrapped($"Viewers: {host.ViewerCount}");
            ImGui.TextWrapped(host.Status);
            ImGui.TextWrapped($"Video: {host.VideoStatus}");

            if (ImGui.Button("Stop hosting", new Vector2(-1, 0)))
            {
                _ = Task.Run(() => host.StopAsync());
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(host?.Status) && host.Status != "Idle")
                ImGui.TextWrapped(host.Status);

            if (ImGui.Button("Start hosting", new Vector2(-1, 0)))
            {
                host?.Dispose();
                host = new StreamHost(backend, relayUrl, log);
                host.StateChanged += () => { };
                _ = Task.Run(() => host.StartAsync());
            }
        }
    }

    private void DrawSpectateSection()
    {
        ImGui.TextUnformatted("Watch a stream");

        // Viewer mode toggle.
        var modes = new[] { "Window", "World screen" };
        var mode = useWorldScreen ? 1 : 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##viewermode", ref mode, modes, modes.Length))
            useWorldScreen = mode == 1;

        if (client is { IsConnected: true })
        {
            ImGui.TextWrapped(client.Status);

            if (ImGui.Button("Stop watching", new Vector2(-1, 0)))
            {
                showViewerWindow = false;
                _ = Task.Run(() => client.DisconnectAsync());
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(client?.Status) && client.Status != "Idle")
                ImGui.TextWrapped(client.Status);

            ImGui.SetNextItemWidth(-80);
            ImGui.InputTextWithHint("##roomcode", "Room code", ref joinCode, 8);
            ImGui.SameLine();

            if (ImGui.Button("Join") && !string.IsNullOrWhiteSpace(joinCode))
            {
                client?.Dispose();
                client = new StreamClient(relayUrl, log);
                client.StateChanged += () =>
                {
                    if (client.IsConnected && !useWorldScreen)
                        showViewerWindow = true;
                };
                _ = Task.Run(() => client.ConnectAsync(joinCode));
            }
        }
    }

    private void DrawRelaySetting()
    {
        ImGui.TextUnformatted("Relay");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##relay", "wss://relay.example.com", ref relayUrl, 512))
        {
            config.RelayUrl = relayUrl;
            saveConfig();
        }

        ImGui.TextWrapped(
            "The relay URL your friends connect through. " +
            "Use wss:// for a Cloudflare Tunnel, ws:// for local/Tailscale.");
    }

    // Draw the spectator viewer window (separate ImGui window).
    public void DrawViewerWindow()
    {
        if (!showViewerWindow || client is not { IsConnected: true })
            return;

        UpdateViewerTexture();

        ImGui.SetNextWindowSize(new Vector2(540, 500), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Stream Viewer", ref showViewerWindow))
        {
            if (viewerTexture != null)
            {
                var avail = ImGui.GetContentRegionAvail();
                var texW = (float)viewerTexture.Width;
                var texH = (float)viewerTexture.Height;
                var aspect = texW / texH;

                var drawW = avail.X;
                var drawH = drawW / aspect;
                if (drawH > avail.Y)
                {
                    drawH = avail.Y;
                    drawW = drawH * aspect;
                }

                ImGui.Image(viewerTexture.Handle, new Vector2(drawW, drawH));
            }
            else
            {
                ImGui.TextWrapped("Waiting for frames...");
            }
        }

        ImGui.End();
    }

    private void UpdateViewerTexture()
    {
        if (client == null)
            return;

        var version = lastViewerVersion;
        if (!client.TryGetFrame(ref version, out var rgba, out var w, out var h))
            return;

        lastViewerVersion = version;

        try
        {
            var spec = RawImageSpecification.Rgba32(w, h);
            var newTex = textureProvider.CreateFromRaw(spec, rgba, "SnesEmulator.StreamViewer");
            viewerTexture?.Dispose();
            viewerTexture = newTex;
        }
        catch (Exception ex)
        {
            log($"Viewer texture error: {ex.Message}");
        }
    }

    // --- Identity-based streaming (sync) ---

    public void GoLive(string uid, string name)
    {
        var backend = getBackend();
        if (backend is not { IsGameLoaded: true })
            return;

        host?.Dispose();
        host = new StreamHost(backend, relayUrl, log);
        host.StateChanged += () => { };
        _ = Task.Run(() => host.GoLiveAsync(uid, name));
    }

    public void StopLive()
    {
        if (host is { IsLive: true })
            _ = Task.Run(() => host.StopLiveAsync());
    }

    public void SubscribeToPlayer(string uid)
    {
        client?.Dispose();
        client = new StreamClient(relayUrl, log);
        client.StateChanged += () =>
        {
            if (client.IsConnected && !useWorldScreen)
                showViewerWindow = true;
        };
        _ = Task.Run(() => client.SubscribeAsync(uid));
    }

    public void Unsubscribe()
    {
        showViewerWindow = false;
        if (client != null)
            _ = Task.Run(() => client.UnsubscribeAsync());
    }

    public void Dispose()
    {
        host?.Dispose();
        host = null;
        client?.Dispose();
        client = null;
        viewerTexture?.Dispose();
        viewerTexture = null;
    }
}
