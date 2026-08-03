using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EmulatorStream;

// Spectator-side streaming client: subscribes to a live player by their
// player ID, receives H.264 + PCM, decodes video to RGBA32, and plays audio.
// The latest decoded frame is exposed for ImGui texture upload.
public sealed class StreamClient : IDisposable
{
    private readonly string relayUrl;
    private readonly Action<string> log;

    private ClientWebSocket? ws;
    private H264Decoder? decoder;
    private CancellationTokenSource? cts;
    private Task? receiveLoop;

    // Audio playback.
    private BufferedWaveProvider? audioBuffer;
    private WasapiOut? audioOutput;
    private int sampleRate = 32000;
    private bool audioEnabled = true;

    // Latest decoded frame (thread-safe via lock).
    private readonly object frameLock = new();
    private byte[]? latestFrame;
    private int frameWidth;
    private int frameHeight;
    private long frameVersion;

    public bool IsConnected { get; private set; }
    public string? SubscribedUid { get; private set; }
    public string? SubscribedPlayerId { get; private set; }
    public string? SubscribedName { get; private set; }
    public string Status { get; private set; } = "Idle";
    public bool AudioEnabled => audioEnabled;

    public event Action? StateChanged;

    public StreamClient(string relayUrl, Action<string> log)
    {
        this.relayUrl = relayUrl;
        this.log = log;
    }

    // Only one watched stream plays audio at a time; the others stay muted.
    public void SetAudioEnabled(bool enabled)
    {
        audioEnabled = enabled;
        if (!enabled)
        {
            audioOutput?.Dispose();
            audioOutput = null;
            audioBuffer = null;
        }
        else if (audioOutput == null && IsConnected)
        {
            InitAudio(sampleRate);
        }
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected)
            return;

        Status = "Disconnecting...";
        StateChanged?.Invoke();

        Cleanup();
        SubscribedUid = null;
        SubscribedPlayerId = null;
        SubscribedName = null;
        Status = "Idle";
        StateChanged?.Invoke();
    }

    // Subscribe to a player's live stream by their player ID ("1234-5678").
    public async Task SubscribeAsync(string playerId)
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
            var uri = new Uri(relayUrl.TrimEnd('/') + "/ws");
            await ws.ConnectAsync(uri, token);
        }
        catch (Exception ex)
        {
            Status = $"Connection failed: {ex.Message}";
            StateChanged?.Invoke();
            Cleanup();
            return;
        }

        var json = new ControlMsg { Action = "subscribe", PlayerId = playerId }.ToJson();
        await ws.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
            WebSocketMessageType.Text, true, token);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        if (result.MessageType == WebSocketMessageType.Text)
        {
            var msg = ControlMsg.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (msg?.Type == "subscribed")
            {
                SubscribedUid = msg.Uid;
                SubscribedPlayerId = msg.PlayerId ?? playerId;
                SubscribedName = msg.Name;
                IsConnected = true;
                Status = $"Watching {SubscribedName ?? SubscribedPlayerId}";
                log($"Subscribed to live stream: {SubscribedName} ({SubscribedPlayerId})");
            }
            else
            {
                Status = $"Subscribe failed: {msg?.Message ?? "player is not live"}";
                StateChanged?.Invoke();
                Cleanup();
                return;
            }
        }

        StateChanged?.Invoke();

        decoder = new H264Decoder();
        receiveLoop = Task.Run(() => ReceiveLoopAsync(token), token);
    }

    public async Task UnsubscribeAsync()
    {
        if (!IsConnected || SubscribedPlayerId == null)
            return;

        try
        {
            if (ws is { State: WebSocketState.Open })
            {
                var json = new ControlMsg { Action = "unsubscribe" }.ToJson();
                await ws.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch { }

        Cleanup();
        SubscribedUid = null;
        SubscribedPlayerId = null;
        SubscribedName = null;
        Status = "Idle";
        StateChanged?.Invoke();
    }

    // Try to get the latest decoded frame.  Returns true if a new frame
    // is available since the last call with this lastVersion.
    public bool TryGetFrame(ref long lastVersion, out byte[] rgba, out int width, out int height)
    {
        lock (frameLock)
        {
            if (frameVersion == lastVersion || latestFrame == null)
            {
                rgba = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }

            lastVersion = frameVersion;
            rgba = (byte[])latestFrame.Clone();
            width = frameWidth;
            height = frameHeight;
            return true;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        // Accumulate partial messages (WebSocket fragments).
        var accumulator = new byte[1024 * 1024];
        var accLen = 0;

        try
        {
            while (!token.IsCancellationRequested && ws is { State: WebSocketState.Open })
            {
                var result = await ws.ReceiveAsync(
                    new ArraySegment<byte>(accumulator, accLen, accumulator.Length - accLen), token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                accLen += result.Count;

                if (!result.EndOfMessage)
                {
                    // Grow the buffer if needed.
                    if (accLen >= accumulator.Length)
                    {
                        var bigger = new byte[accumulator.Length * 2];
                        Array.Copy(accumulator, bigger, accLen);
                        accumulator = bigger;
                    }
                    continue;
                }

                // Complete message received.
                if (result.MessageType == WebSocketMessageType.Binary && accLen > 1)
                {
                    HandleBinary(accumulator.AsSpan(0, accLen));
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(accumulator, 0, accLen);
                    HandleControl(json);
                }

                accLen = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log($"Stream receive error: {ex.Message}");
        }

        if (!token.IsCancellationRequested)
        {
            IsConnected = false;
            Status = "Stream ended";
            StateChanged?.Invoke();
        }
    }

    private void HandleBinary(ReadOnlySpan<byte> data)
    {
        var type = data[0];
        var payload = data.Slice(1);

        switch (type)
        {
            case StreamProtocol.MsgStreamInfo:
            {
                var info = StreamProtocol.ParseStreamInfo(payload);
                if (info != null)
                {
                    sampleRate = info.SampleRate > 0 ? info.SampleRate : 32000;
                    if (audioEnabled)
                        InitAudio(sampleRate);
                    log($"Stream info: {info.Width}x{info.Height} @ {info.Fps}fps, audio {sampleRate}Hz");
                }
                break;
            }

            case StreamProtocol.MsgVideo:
            {
                if (decoder == null)
                    break;

                var rgba = decoder.Decode(payload, out var w, out var h);
                if (rgba != null)
                {
                    lock (frameLock)
                    {
                        latestFrame = rgba;
                        frameWidth = w;
                        frameHeight = h;
                        frameVersion++;
                    }
                }
                break;
            }

            case StreamProtocol.MsgAudio:
            {
                audioBuffer?.AddSamples(payload.ToArray(), 0, payload.Length);
                break;
            }
        }
    }

    private void HandleControl(string json)
    {
        var msg = ControlMsg.Parse(json);
        if (msg?.Type is "closed" or "live_ended")
        {
            IsConnected = false;
            Status = msg.Type == "live_ended" ? "Player stopped streaming" : "Host ended the stream";
            SubscribedUid = null;
            SubscribedPlayerId = null;
            SubscribedName = null;
            StateChanged?.Invoke();
            Cleanup();
        }
    }

    private void InitAudio(int rate)
    {
        if (audioOutput != null)
            return;

        try
        {
            var format = new WaveFormat(rate, 16, 2);
            audioBuffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(300),
            };
            audioOutput = new WasapiOut(AudioClientShareMode.Shared, 40);
            audioOutput.Init(audioBuffer);
            audioOutput.Play();
        }
        catch (Exception ex)
        {
            log($"Stream audio init failed: {ex.Message}");
        }
    }

    private void Cleanup()
    {
        cts?.Cancel();
        try { receiveLoop?.Wait(1000); } catch { }
        cts?.Dispose();
        cts = null;
        receiveLoop = null;

        decoder?.Dispose();
        decoder = null;

        audioOutput?.Dispose();
        audioOutput = null;
        audioBuffer = null;

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

        lock (frameLock)
        {
            latestFrame = null;
            frameVersion = 0;
        }
    }

    public void Dispose()
    {
        Cleanup();
    }
}
