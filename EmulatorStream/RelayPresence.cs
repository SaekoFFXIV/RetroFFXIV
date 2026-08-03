using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmulatorStream;

// Keeps a persistent presence connection to the relay so the plugin can
// show who is online.  Identifies with the registered player ID, polls the
// online list every few seconds, and reconnects with backoff on failure.
public sealed class RelayPresence : IDisposable
{
    private const int PollIntervalSeconds = 10;
    private const int RetryDelaySeconds = 5;

    private readonly Action<string> log;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private Task? loop;
    private string? startKey;

    private readonly object stateLock = new();
    private List<OnlinePlayerInfo> online = new();

    public bool IsConnected { get; private set; }

    public RelayPresence(Action<string> log)
    {
        this.log = log;
    }

    public List<OnlinePlayerInfo> GetOnline()
    {
        lock (stateLock)
            return online.ToList();
    }

    // The Friends tab needs the global presence total every frame. Avoid
    // cloning the online roster just to display that one number.
    public int OnlineCount
    {
        get { lock (stateLock) { return online.Count; } }
    }

    // Idempotent: a no-op while running with the same identity/relay.
    public void Start(string relayUrl, string uid, string playerId, string name)
    {
        var key = $"{relayUrl}|{uid}|{playerId}";
        if (key == startKey && loop is { IsCompleted: false })
            return;

        Stop();
        startKey = key;
        cts = new CancellationTokenSource();
        loop = Task.Run(() => LoopAsync(relayUrl, uid, playerId, name, cts.Token));
    }

    public void Stop()
    {
        startKey = null;
        cts?.Cancel();
        try { loop?.Wait(1000); } catch { }
        cts?.Dispose();
        cts = null;
        loop = null;
        IsConnected = false;
        lock (stateLock)
            online.Clear();
    }

    private async Task LoopAsync(string relayUrl, string uid, string playerId, string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                ws = socket;
                await socket.ConnectAsync(new Uri(relayUrl.TrimEnd('/') + "/ws"), token);

                var presence = new ControlMsg
                {
                    Action = "presence",
                    Uid = uid,
                    PlayerId = playerId,
                    Name = name,
                }.ToJson();
                await socket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(presence)),
                    WebSocketMessageType.Text, true, token);

                var buffer = new byte[16384];
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                var ack = result.MessageType == WebSocketMessageType.Text
                    ? ControlMsg.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))
                    : null;
                if (ack?.Type != "presence_ok")
                {
                    log($"Presence rejected: {ack?.Message ?? "no ack"}");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), token);
                    continue;
                }

                IsConnected = true;

                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(
                            new ControlMsg { Action = "list_online" }.ToJson())),
                        WebSocketMessageType.Text, true, token);

                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = ControlMsg.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (msg?.Type == "online" && msg.Online != null)
                        {
                            lock (stateLock)
                                online = msg.Online;
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log($"Presence connection error: {ex.Message}");
            }

            IsConnected = false;
            lock (stateLock)
                online.Clear();

            try { await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), token); }
            catch (OperationCanceledException) { break; }
        }

        IsConnected = false;
    }

    public void Dispose() => Stop();
}
