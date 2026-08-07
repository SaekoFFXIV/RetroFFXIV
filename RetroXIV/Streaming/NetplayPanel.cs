using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using System.Threading.Tasks;
using RetroXIV.Emulation;

namespace RetroXIV.Streaming;

// ImGui UI for the "Netplay" tab: host or join a lockstep netplay session.
internal sealed class NetplayPanel : IDisposable
{
    private readonly Configuration config;
    private readonly Action<string> log;
    private readonly Func<RetroCore?> getCore;
    private readonly InputManager inputManager;
    private readonly Func<string> getPlayerUid;

    private NetplaySession? session;
    private string joinCode = string.Empty;
    private string relayUrl = string.Empty;

    public bool IsActive => session is { IsConnected: true };

    // Exposed so EmulatorService can hook the PreFrame callback.
    public NetplaySession? Session => session;

    public NetplayPanel(
        Configuration config,
        Action<string> log,
        Func<RetroCore?> getCore,
        InputManager inputManager,
        Func<string> getPlayerUid)
    {
        this.config = config;
        this.log = log;
        this.getCore = getCore;
        this.inputManager = inputManager;
        this.getPlayerUid = getPlayerUid;
        relayUrl = config.RelayUrl;
    }

    public void DrawTab()
    {
        var core = getCore();
        if (core is not { IsGameLoaded: true })
        {
            ImGui.TextWrapped("Load a game first. Both players must use the same ROM.");
            return;
        }

        // The lockstep protocol is SNES-only (16-bit digital joypad, bsnes
        // determinism); keep it firmly off for PS1 and any future platforms.
        if (!core.SupportsNetplay)
        {
            ImGui.TextWrapped("Netplay currently supports SNES (bsnes) games only.");
            return;
        }

        if (session is { IsConnected: true })
        {
            DrawActiveSession();
        }
        else
        {
            DrawSetup();
        }

    }

    private void DrawActiveSession()
    {
        ImGui.TextWrapped($"Room: {session!.RoomCode}");
        ImGui.TextWrapped($"Your slot: {session.LocalSlot} (Player {session.LocalSlot + 1})");

        var peerStatus = session.PeerConnected ? "Connected" : "Waiting...";
        var peerColor = session.PeerConnected ? 0xFF60FF40u : 0xFF8A8A94u;
        ImGui.TextUnformatted("Opponent: ");
        ImGui.SameLine();
        ImGui.TextColored(BitColor(peerColor), peerStatus);

        ImGui.TextWrapped(session.Status);

        ImGui.Spacing();
        if (ImGui.Button("Leave session", new Vector2(-1, 0)))
        {
            inputManager.LocalPort = 0;
            _ = Task.Run(() => session.DisconnectAsync());
        }
    }

    private void DrawSetup()
    {
        if (!string.IsNullOrEmpty(session?.Status) && session.Status != "Idle")
            ImGui.TextWrapped(session.Status);

        if (ImGui.Button("Host netplay", new Vector2(-1, 0)))
        {
            StartSession(host: true);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-80);
        ImGui.InputTextWithHint("##npcode", "Room code", ref joinCode, 8);
        ImGui.SameLine();
        if (ImGui.Button("Join") && !string.IsNullOrWhiteSpace(joinCode))
        {
            StartSession(host: false);
        }
    }

    private void StartSession(bool host)
    {
        session?.Dispose();
        session = new NetplaySession(relayUrl, getPlayerUid(), log);
        session.StateChanged += OnSessionStateChanged;

        if (host)
        {
            _ = Task.Run(() => session.HostAsync());
        }
        else
        {
            _ = Task.Run(() => session.JoinAsync(joinCode));
        }
    }

    private void OnSessionStateChanged()
    {
        if (session is { IsConnected: true })
        {
            inputManager.LocalPort = session.LocalSlot;
        }
    }

    // Netplay is an authenticated feature. If XIVAuth is removed while a
    // session is active, leave cleanly instead of continuing anonymously.
    public void StopForAuthLoss()
    {
        if (session == null)
            return;

        inputManager.LocalPort = 0;
        _ = Task.Run(() => session.DisconnectAsync());
    }

    private static Vector4 BitColor(uint rgba)
    {
        var r = ((rgba >> 0) & 0xFF) / 255f;
        var g = ((rgba >> 8) & 0xFF) / 255f;
        var b = ((rgba >> 16) & 0xFF) / 255f;
        var a = ((rgba >> 24) & 0xFF) / 255f;
        return new Vector4(r, g, b, a);
    }

    public void Dispose()
    {
        session?.Dispose();
        session = null;
    }
}
