using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace EmulatorStream;

// ImGui UI for hosting and watching streams.  Hosting is identity-based
// (go live under a player ID — no room codes).  Watching supports up to
// MaxStreams concurrent subscriptions; each gets its own viewer window and
// can also be rendered on a world screen by the hosting plugin.
public sealed class StreamPanel : IDisposable
{
    public const int MaxStreams = 4;

    private readonly StreamConfig config;
    private readonly ITextureProvider textureProvider;
    private readonly Action<string> log;
    private readonly Func<IEmulatorBackend?> getBackend;
    private readonly Action saveConfig;

    private StreamHost? host;
    private string relayUrl = string.Empty;

    // One entry per watched stream, keyed by player ID.
    private sealed class Viewer
    {
        public required StreamClient Client;
        public IDalamudTextureWrap? Texture;
        public long TextureVersion = -1;
        public bool ShowWindow = true;
        public bool Disposed;
    }

    private readonly Dictionary<string, Viewer> viewers = new();
    private readonly List<string> removeQueue = new();

    public bool IsLive => host?.IsLive == true;
    public int WatchCount => viewers.Count;
    public string RelayUrl => relayUrl;
    public StreamHost? GetStreamHost() => host;
    public IReadOnlyCollection<StreamClient> Clients => viewers.Values.Select(v => v.Client).ToList();

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

    // Draw the shared streaming settings (relay URL) inside the Sync tab.
    public void DrawTab()
    {
        DrawRelaySetting();
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

    // --- Hosting (identity-based) ---

    public void GoLive(string uid, string name, string playerId)
    {
        var backend = getBackend();
        if (backend is not { IsGameLoaded: true })
            return;

        host?.Dispose();
        host = new StreamHost(backend, relayUrl, log);
        host.StateChanged += () => { };
        _ = Task.Run(() => host.GoLiveAsync(uid, name, playerId));
    }

    public void StopLive()
    {
        if (host is { IsLive: true })
            _ = Task.Run(() => host.StopLiveAsync());
    }

    // End the whole session: stop hosting and drop every watched stream
    // (viewer windows hide, world screens lose their clients).
    public void StopAll()
    {
        StopLive();
        foreach (var key in viewers.Keys.ToList())
            StopWatching(key);
    }

    // --- Watching (up to MaxStreams concurrent) ---

    public bool IsWatching(string playerId) => viewers.ContainsKey(NormalizeId(playerId));

    public StreamClient? GetClient(string playerId) =>
        viewers.TryGetValue(NormalizeId(playerId), out var v) ? v.Client : null;

    // Start watching a player.  Returns false with an error when the
    // 4-screen cap is reached (or the ID is already being watched).
    public bool TryWatch(string playerId, out string? error)
    {
        error = null;
        var key = NormalizeId(playerId);

        if (viewers.ContainsKey(key))
            return true;

        if (viewers.Count >= MaxStreams)
        {
            error = $"Limit reached — you can watch up to {MaxStreams} screens at a time.";
            return false;
        }

        // Audio follows the newest stream; mute the others.
        foreach (var v in viewers.Values)
            v.Client.SetAudioEnabled(false);

        var client = new StreamClient(relayUrl, log);
        var viewer = new Viewer { Client = client };
        viewers[key] = viewer;

        client.StateChanged += () =>
        {
            if (!client.IsConnected && !string.IsNullOrEmpty(client.Status)
                && client.Status is not "Idle" and not "Connecting..." and not "Disconnecting...")
            {
                // Connection ended (live_ended, error, disconnect): drop the entry.
                lock (removeQueue)
                {
                    if (!removeQueue.Contains(key))
                        removeQueue.Add(key);
                }
            }
        };

        _ = Task.Run(() => client.SubscribeAsync(playerId));
        return true;
    }

    public void StopWatching(string playerId)
    {
        var key = NormalizeId(playerId);
        if (!viewers.TryGetValue(key, out var viewer))
            return;

        viewer.ShowWindow = false;
        _ = Task.Run(async () =>
        {
            if (viewer.Client.IsConnected)
                await viewer.Client.UnsubscribeAsync();
            lock (removeQueue)
            {
                if (!removeQueue.Contains(key))
                    removeQueue.Add(key);
            }
        });
    }

    public void SetWindowVisible(string playerId, bool visible)
    {
        if (viewers.TryGetValue(NormalizeId(playerId), out var viewer))
            viewer.ShowWindow = visible;
    }

    public bool IsWindowVisible(string playerId) =>
        viewers.TryGetValue(NormalizeId(playerId), out var viewer) && viewer.ShowWindow;

    // Drop watchers whose connection ended since the last pass.  Public so
    // the world-screen pass can reconcile even while the deck is closed.
    public void FlushRemoveQueue()
    {
        List<string> pending;
        lock (removeQueue)
        {
            if (removeQueue.Count == 0)
                return;
            pending = new List<string>(removeQueue);
            removeQueue.Clear();
        }

        foreach (var key in pending)
        {
            if (!viewers.Remove(key, out var viewer))
                continue;
            viewer.Disposed = true;
            viewer.Client.Dispose();
            viewer.Texture?.Dispose();
            viewer.Texture = null;
            log($"Stopped watching {key}");
        }
    }

    // Draw one viewer window per watched stream.
    public void DrawViewerWindows()
    {
        FlushRemoveQueue();

        foreach (var (key, viewer) in viewers)
        {
            if (!viewer.ShowWindow)
            {
                // Still refresh the texture so world screens keep updating
                // even while the window is hidden.
                UpdateViewerTexture(viewer);
                continue;
            }

            UpdateViewerTexture(viewer);

            var name = viewer.Client.SubscribedName ?? key;
            var open = viewer.ShowWindow;

            ImGui.SetNextWindowSize(new Vector2(540, 500), ImGuiCond.FirstUseEver);
            if (ImGui.Begin($"Stream Viewer — {name}##viewer_{key}", ref open))
            {
                if (viewer.Texture != null)
                {
                    var avail = ImGui.GetContentRegionAvail();
                    var texW = (float)viewer.Texture.Width;
                    var texH = (float)viewer.Texture.Height;
                    var aspect = texW / texH;

                    var drawW = avail.X;
                    var drawH = drawW / aspect;
                    if (drawH > avail.Y)
                    {
                        drawH = avail.Y;
                        drawW = drawH * aspect;
                    }

                    ImGui.Image(viewer.Texture.Handle, new Vector2(drawW, drawH));
                }
                else
                {
                    ImGui.TextWrapped("Waiting for frames...");
                }
            }

            ImGui.End();

            // Closing the window hides it; the subscription stays alive so
            // the stream can keep playing on a world screen.
            if (!open)
                viewer.ShowWindow = false;
        }
    }

    private void UpdateViewerTexture(Viewer viewer)
    {
        var version = viewer.TextureVersion;
        if (!viewer.Client.TryGetFrame(ref version, out var rgba, out var w, out var h))
            return;

        viewer.TextureVersion = version;

        try
        {
            var spec = RawImageSpecification.Rgba32(w, h);
            var newTex = textureProvider.CreateFromRaw(spec, rgba, $"SnesEmulator.StreamViewer.{w}x{h}");
            viewer.Texture?.Dispose();
            viewer.Texture = newTex;
        }
        catch (Exception ex)
        {
            log($"Viewer texture error: {ex.Message}");
        }
    }

    // Player IDs compare on their core chars only (dash/case insensitive).
    public static string NormalizeId(string value) => PlayerIds.Normalize(value);

    public void Dispose()
    {
        host?.Dispose();
        host = null;
        foreach (var viewer in viewers.Values)
        {
            viewer.Client.Dispose();
            viewer.Texture?.Dispose();
        }
        viewers.Clear();
    }
}
