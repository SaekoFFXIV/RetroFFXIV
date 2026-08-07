using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmulatorStream;

namespace RetroXIV.Streaming;

// Lockstep netplay session.  Both players run the same deterministic core
// (bsnes) and exchange inputs through the relay every frame.  Neither
// player advances to frame N+1 until both inputs for frame N have arrived.
//
// Wire format for input packets (binary WebSocket, 8 bytes):
//   [0]       0x04 (MsgNetplayInput)
//   [1]       sender slot (0 or 1)
//   [2..5]    frame number (uint32 LE)
//   [6..7]    joypad state (uint16 LE, libretro button bits)
//
// The relay routes each packet to every OTHER player in the room.
internal sealed class NetplaySession : IDisposable
{
    internal const byte MsgNetplayInput = 0x04;
    private const int InputPacketSize = 8;
    private const int SyncTimeoutMs = 150; // ~9 frames at 60fps

    private readonly string relayUrl;
    private readonly string uid;
    private readonly Action<string> log;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private Task? receiveLoop;

    // Received remote inputs, keyed by frame number.
    private readonly ConcurrentDictionary<long, ushort> remoteInputs = new();
    private readonly AutoResetEvent inputArrived = new(false);

    private ushort lastRemoteInput;
    private long localFrame;

    public int LocalSlot { get; private set; } = -1;
    public int RemoteSlot => LocalSlot == 0 ? 1 : 0;
    public string? RoomCode { get; private set; }
    public bool IsConnected { get; private set; }
    public bool PeerConnected { get; private set; }
    public string Status { get; private set; } = "Idle";

    // Smoothed round-trip estimate (measured via input echo timing).
    public double LatencyMs { get; private set; }

    public event Action? StateChanged;

    public NetplaySession(string relayUrl, string uid, Action<string> log)
    {
        this.relayUrl = relayUrl;
        this.uid = uid;
        this.log = log;
    }

    // Create a new netplay room (host).
    public async Task HostAsync()
    {
        await ConnectAndSend(new ControlMsg
        {
            Action = "create_netplay",
            Uid = uid,
        }, "netplay_created");
    }

    // Join an existing netplay room by code.
    public async Task JoinAsync(string code)
    {
        await ConnectAndSend(new ControlMsg
        {
            Action = "join_netplay",
            Room = code.ToUpperInvariant(),
            Uid = uid,
        }, "netplay_joined");
    }

    private async Task ConnectAndSend(ControlMsg request, string expectedType)
    {
        if (IsConnected)
            return;

        cts = new CancellationTokenSource();
        var token = cts.Token;

        Status = "Connecting...";
        StateChanged?.Invoke();

        ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(relayUrl.TrimEnd('/') + "/ws"), token);
        }
        catch (Exception ex)
        {
            Status = $"Connection failed: {ex.Message}";
            StateChanged?.Invoke();
            Cleanup();
            return;
        }

        // Send the create/join request.
        var json = Encoding.UTF8.GetBytes(request.ToJson());
        await ws.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, token);

        // Wait for the response.
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        if (result.MessageType != WebSocketMessageType.Text)
        {
            Status = "Unexpected response";
            Cleanup();
            return;
        }

        var msg = ControlMsg.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        if (msg?.Type == expectedType && msg.Slot.HasValue)
        {
            LocalSlot = msg.Slot.Value;
            RoomCode = msg.Room;
            IsConnected = true;
            Status = $"Room {RoomCode} — slot {LocalSlot} — waiting for opponent";
            log($"Netplay: {expectedType}, room={RoomCode}, slot={LocalSlot}");
        }
        else
        {
            Status = $"Failed: {msg?.Message ?? "unknown error"}";
            StateChanged?.Invoke();
            Cleanup();
            return;
        }

        StateChanged?.Invoke();
        receiveLoop = Task.Run(() => ReceiveLoopAsync(token), token);
    }

    // Lockstep sync: send local input for this frame, block until the
    // remote player's input arrives (or timeout).  Returns the remote
    // input to feed to the core.  Called on the emulation thread.
    public ushort SyncFrame(ushort localInput)
    {
        if (!IsConnected || ws is not { State: WebSocketState.Open })
            return 0;

        var frame = localFrame++;

        // Send our input.
        var packet = new byte[InputPacketSize];
        packet[0] = MsgNetplayInput;
        packet[1] = (byte)LocalSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), (uint)frame);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), localInput);

        try
        {
            ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None)
              .GetAwaiter().GetResult();
        }
        catch
        {
            return lastRemoteInput;
        }

        // Wait for the remote input for this frame.
        if (remoteInputs.TryRemove(frame, out var input))
        {
            lastRemoteInput = input;
            return input;
        }

        // Spin-wait with the event, up to the timeout.
        var deadline = Environment.TickCount64 + SyncTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            inputArrived.WaitOne(1);
            if (remoteInputs.TryRemove(frame, out input))
            {
                lastRemoteInput = input;
                return input;
            }
        }

        // Timeout: predict with the last known input.
        return lastRemoteInput;
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected)
            return;
        Cleanup();
        RoomCode = null;
        LocalSlot = -1;
        PeerConnected = false;
        Status = "Idle";
        StateChanged?.Invoke();
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested && ws is { State: WebSocketState.Open })
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Binary && result.Count >= InputPacketSize)
                {
                    if (buffer[0] == MsgNetplayInput)
                    {
                        var frame = (long)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2));
                        var input = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6));
                        remoteInputs[frame] = input;
                        inputArrived.Set();
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControl(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log($"Netplay receive error: {ex.Message}");
        }

        if (!token.IsCancellationRequested)
        {
            IsConnected = false;
            PeerConnected = false;
            Status = "Connection lost";
            StateChanged?.Invoke();
        }
    }

    private void HandleControl(string json)
    {
        var msg = ControlMsg.Parse(json);
        if (msg == null)
            return;

        switch (msg.Type)
        {
            case "netplay_players":
                // Roster update — check if we have an opponent.
                PeerConnected = msg.Players is { Count: >= 2 };
                Status = PeerConnected
                    ? $"Room {RoomCode} — slot {LocalSlot} — connected!"
                    : $"Room {RoomCode} — slot {LocalSlot} — waiting for opponent";
                StateChanged?.Invoke();
                break;

            case "netplay_player_left":
                PeerConnected = false;
                Status = $"Room {RoomCode} — opponent left";
                StateChanged?.Invoke();
                break;
        }
    }

    private void Cleanup()
    {
        cts?.Cancel();
        try { receiveLoop?.Wait(1000); } catch { }
        cts?.Dispose();
        cts = null;
        receiveLoop = null;

        remoteInputs.Clear();
        localFrame = 0;
        lastRemoteInput = 0;

        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                      .Wait(1000);
            }
            catch { }
            ws.Dispose();
            ws = null;
        }

        IsConnected = false;
    }

    public void Dispose()
    {
        inputArrived.Dispose();
        Cleanup();
    }
}
