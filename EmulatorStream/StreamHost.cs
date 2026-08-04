using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmulatorStream;

// Hosts a streaming session: captures the emulator framebuffer at 30 fps,
// integer-upscales it, H.264-encodes, and pushes to the relay over WebSocket.
// Audio is tapped from the core's ring buffer and sent as raw PCM.
// Identity-based: the host goes live under their player ID — no room codes.
public sealed class StreamHost : IDisposable
{
    private const int StreamScale = 3;
    private const float StreamFps = 30f;

    // Sized for ~600+ concurrent spectators over a 1 Gbps relay uplink
    // (~1.6 Mbps budget per viewer, worst case).
    private const int TargetBitrate = 1_200_000; // 1.2 Mbps

    private const int AudioChunkFrames = 3200;   // ~100 ms at 32 kHz

    // Opus stream audio: 48 kHz stereo at 64 Kbps replaces the raw PCM
    // (~1 Mbps) with ~0.06 Mbps.  Opus frame size is fixed at 20 ms.
    public const int OpusSampleRate = 48000;
    private const int OpusChannels = 2;
    private const int OpusBitrate = 64_000;
    private const int OpusFrameSamples = 960;    // 20 ms at 48 kHz

    private readonly IEmulatorBackend backend;
    private readonly string relayUrl;
    private readonly Action<string> log;

    private ClientWebSocket? ws;
    private H264Encoder? encoder;
    private OpusEncoder? opusEncoder;
    private string audioCodec = StreamProtocol.AudioCodecOpus;
    private CancellationTokenSource? cts;
    private Task? sendLoop;
    private Task? audioLoop;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly object screenStateLock = new();
    private WorldScreenState? screenState;

    private byte[] upscaleBuf = Array.Empty<byte>();

    public string? PlayerId { get; private set; }
    public int ViewerCount { get; private set; }
    public bool IsHosting { get; private set; }
    public bool IsLive { get; private set; }
    public string Status { get; private set; } = "Idle";
    public string VideoStatus { get; private set; } = "Not started";

    public event Action? StateChanged;

    public StreamHost(IEmulatorBackend backend, string relayUrl, Action<string> log)
    {
        this.backend = backend;
        this.relayUrl = relayUrl;
        this.log = log;
    }

    // Identity-based streaming: go live with a persistent_key and player ID.
    public async Task GoLiveAsync(string uid, string name, string playerId, WorldScreenState? initialScreen)
    {
        if (IsHosting || IsLive)
            return;

        lock (screenStateLock)
            screenState = initialScreen?.Clone();

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

        WorldScreenState? initialState;
        lock (screenStateLock)
            initialState = screenState?.Clone();
        await SendControlAsync(
            new ControlMsg
            {
                Action = "go_live",
                Uid = uid,
                PlayerId = playerId,
                Name = name,
                Screen = initialState,
            }, token);

        var response = await ReceiveControlAsync(token);
        if (response?.Type == "live_started")
        {
            IsLive = true;
            IsHosting = true;
            PlayerId = response.PlayerId ?? playerId;
            ViewerCount = response.Subscribers ?? 0;
            Status = $"Live as {name} — {ViewerCount} viewer{(ViewerCount == 1 ? "" : "s")}";
            log($"Went live: player_id={PlayerId}, name={name}");
        }
        else
        {
            Status = $"Failed to go live: {response?.Message ?? "unknown error"}";
            StateChanged?.Invoke();
            Cleanup();
            return;
        }

        StateChanged?.Invoke();

        // Decide the audio codec up front so the stream info is accurate.
        try
        {
            opusEncoder = new OpusEncoder(OpusSampleRate, OpusChannels, OpusBitrate);
            audioCodec = StreamProtocol.AudioCodecOpus;
        }
        catch (Exception ex)
        {
            opusEncoder = null;
            audioCodec = StreamProtocol.AudioCodecPcm;
            log($"Opus unavailable ({ex.Message}) — streaming audio as raw PCM");
        }

        var info = StreamProtocol.PackStreamInfo(
            backend.BaseWidth * StreamScale,
            backend.BaseHeight * StreamScale,
            StreamFps,
            audioCodec == StreamProtocol.AudioCodecOpus ? OpusSampleRate : (int)backend.SampleRate,
            audioCodec);
        await SendBinaryAsync(info, token);

        sendLoop = Task.Run(() => VideoLoopAsync(token), token);
        audioLoop = Task.Run(() => AudioLoopAsync(token), token);
        _ = Task.Run(() => ControlLoopAsync(token), token);
    }

    public async Task StopLiveAsync()
    {
        if (!IsLive)
            return;

        Status = "Stopping...";
        StateChanged?.Invoke();

        try
        {
            if (ws is { State: WebSocketState.Open })
                await SendControlAsync(new ControlMsg { Action = "stop_live" }, CancellationToken.None);
        }
        catch { }

        Cleanup();
        IsLive = false;
        ViewerCount = 0;
        Status = "Idle";
        StateChanged?.Invoke();
    }

    // The host owns the physical screen. This is intentionally independent
    // from IsLive visibility: a host may hide their local screen while still
    // giving spectators its authoritative placed position.
    public void PublishScreenState(WorldScreenState? state)
    {
        WorldScreenState? snapshot;
        lock (screenStateLock)
        {
            screenState = state?.Clone();
            snapshot = screenState?.Clone();
        }

        if (!IsLive || cts is not { IsCancellationRequested: false })
            return;

        var publishToken = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await SendControlAsync(
                    new ControlMsg { Action = "screen_state", Screen = snapshot }, publishToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log($"Screen-state update failed: {ex.Message}"); }
        });
    }

    private async Task VideoLoopAsync(CancellationToken token)
    {
        var frameInterval = TimeSpan.FromSeconds(1.0 / StreamFps);
        var nextFrame = DateTime.UtcNow + frameInterval;
        var nextKeyframe = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        long lastVersion = -1;

        // Force the first frame to be a keyframe.
        encoder?.ForceKeyFrame();

        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (now < nextFrame)
            {
                await Task.Delay(nextFrame - now, token);
            }

            nextFrame += frameInterval;
            if (DateTime.UtcNow - nextFrame > frameInterval)
                nextFrame = DateTime.UtcNow + frameInterval;

            if (!backend.IsGameLoaded)
            {
                VideoStatus = "No game loaded";
                continue;
            }

            var version = backend.FrameVersion;
            if (version == lastVersion)
                continue;

            if (!backend.TryGetFrame(out var rgba, out var srcW, out var srcH))
            {
                VideoStatus = "TryGetFrame failed";
                continue;
            }

            lastVersion = version;

            try
            {
                var dstW = srcW * StreamScale;
                var dstH = srcH * StreamScale;

                if (encoder == null)
                {
                    VideoStatus = $"Creating encoder {dstW}x{dstH}...";
                    encoder = new H264Encoder(dstW, dstH, StreamFps, TargetBitrate);
                    VideoStatus = "Encoder created";
                }

                // Periodic keyframe for late/re-joining spectators.
                if (DateTime.UtcNow >= nextKeyframe)
                {
                    encoder.ForceKeyFrame();
                    nextKeyframe = DateTime.UtcNow + TimeSpan.FromSeconds(2);

                    // Re-send stream info with each keyframe so late
                    // subscribers can initialise format + audio.
                    if (ws is { State: WebSocketState.Open })
                    {
                        var info = StreamProtocol.PackStreamInfo(
                            backend.BaseWidth * StreamScale,
                            backend.BaseHeight * StreamScale,
                            StreamFps,
                            audioCodec == StreamProtocol.AudioCodecOpus ? OpusSampleRate : (int)backend.SampleRate,
                            audioCodec);
                        await SendBinaryAsync(info, token);
                    }
                }

                var upscaled = Upscale(rgba, srcW, srcH, dstW, dstH);
                var h264 = encoder.Encode(upscaled, out _);

                if (h264 is { Length: > 0 } && ws is { State: WebSocketState.Open })
                {
                    await SendBinaryAsync(StreamProtocol.PackVideo(h264), token);
                    VideoStatus = $"Streaming {h264.Length}B/frame";
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                VideoStatus = $"ERROR: {ex.Message}";
                log($"Stream video error: {ex.Message}");
            }
        }
    }

    private async Task AudioLoopAsync(CancellationToken token)
    {
        var chunkInterval = TimeSpan.FromSeconds(AudioChunkFrames / backend.SampleRate);
        var scratch = new short[AudioChunkFrames * 2];

        if (opusEncoder == null)
        {
            // Legacy raw-PCM fallback for when Opus is unavailable.
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(chunkInterval, token);

                if (!backend.IsGameLoaded)
                    continue;

                var frames = backend.ReadStreamAudio(scratch, AudioChunkFrames);
                if (frames <= 0)
                    continue;

                var byteCount = frames * 4; // stereo int16
                var pcm = new byte[byteCount];
                Buffer.BlockCopy(scratch, 0, pcm, 0, byteCount);

                try
                {
                    if (ws is { State: WebSocketState.Open })
                        await SendBinaryAsync(StreamProtocol.PackAudio(pcm), token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
            return;
        }

        var srcRate = (int)backend.SampleRate;
        var resampler = new StereoResampler(srcRate, OpusSampleRate);
        // Worst-case resample ratio assumes an 8 kHz core output.
        var maxOutFrames = AudioChunkFrames * (OpusSampleRate / 8000) + 8;
        var resampled = new short[maxOutFrames * 2];
        var pending = new short[(maxOutFrames + OpusFrameSamples) * 2];
        var pendingFrames = 0;

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(chunkInterval, token);

            if (!backend.IsGameLoaded)
                continue;

            // Cores can change the output rate on load; keep the ratio honest.
            if ((int)backend.SampleRate != srcRate)
            {
                srcRate = (int)backend.SampleRate;
                resampler = new StereoResampler(srcRate, OpusSampleRate);
                pendingFrames = 0;
            }

            var frames = backend.ReadStreamAudio(scratch, AudioChunkFrames);
            if (frames <= 0)
                continue;

            var outFrames = resampler.Resample(
                scratch.AsSpan(0, frames * 2), frames, resampled, maxOutFrames);
            if (outFrames <= 0)
                continue;

            Array.Copy(resampled, 0, pending, pendingFrames * 2, outFrames * 2);
            pendingFrames += outFrames;

            try
            {
                var tlv = new List<byte>((pendingFrames / OpusFrameSamples) * 96);
                var consumed = 0;

                while (pendingFrames - consumed >= OpusFrameSamples)
                {
                    var packet = opusEncoder.Encode(pending.AsSpan(consumed * 2), OpusFrameSamples);
                    tlv.Add((byte)(packet.Length & 0xFF));
                    tlv.Add((byte)(packet.Length >> 8));
                    tlv.AddRange(packet);
                    consumed += OpusFrameSamples;
                }

                if (consumed > 0)
                {
                    var remaining = pendingFrames - consumed;
                    Array.Copy(pending, consumed * 2, pending, 0, remaining * 2);
                    pendingFrames = remaining;
                }

                if (tlv.Count > 0 && ws is { State: WebSocketState.Open })
                    await SendBinaryAsync(StreamProtocol.PackAudioOpus(CollectionsMarshal.AsSpan(tlv)), token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log($"Stream audio error: {ex.Message}"); }
        }
    }

    private async Task ControlLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested && ws is { State: WebSocketState.Open })
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = ControlMsg.Parse(json);
                    if (msg?.Type == "viewers" && msg.Count.HasValue)
                    {
                        var prev = ViewerCount;
                        ViewerCount = msg.Count.Value;
                        Status = $"Live — {ViewerCount} viewer{(ViewerCount == 1 ? "" : "s")}";
                        // Force a keyframe so new/rejoining spectators can decode immediately.
                        if (ViewerCount > prev)
                            encoder?.ForceKeyFrame();
                        StateChanged?.Invoke();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log($"Stream control error: {ex.Message}");
        }

        if (!token.IsCancellationRequested)
        {
            IsHosting = false;
            Status = "Connection lost";
            StateChanged?.Invoke();
        }
    }

    // Nearest-neighbour integer upscale: each source pixel becomes a
    // StreamScale×StreamScale block in the destination.
    private byte[] Upscale(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var needed = dstW * dstH * 4;
        if (upscaleBuf.Length < needed)
            upscaleBuf = new byte[needed];

        for (var sy = 0; sy < srcH; sy++)
        {
            for (var sx = 0; sx < srcW; sx++)
            {
                var si = (sy * srcW + sx) * 4;
                var r = src[si];
                var g = src[si + 1];
                var b = src[si + 2];

                for (var dy = 0; dy < StreamScale; dy++)
                {
                    var rowOff = ((sy * StreamScale + dy) * dstW + sx * StreamScale) * 4;
                    for (var dx = 0; dx < StreamScale; dx++)
                    {
                        var di = rowOff + dx * 4;
                        upscaleBuf[di] = r;
                        upscaleBuf[di + 1] = g;
                        upscaleBuf[di + 2] = b;
                        upscaleBuf[di + 3] = 0xFF;
                    }
                }
            }
        }

        return upscaleBuf;
    }

    private async Task SendControlAsync(ControlMsg msg, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
        await SendAsync(bytes, WebSocketMessageType.Text, token);
    }

    private async Task SendBinaryAsync(byte[] data, CancellationToken token)
    {
        await SendAsync(data, WebSocketMessageType.Binary, token);
    }

    private async Task SendAsync(byte[] data, WebSocketMessageType messageType, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try
        {
            if (ws is { State: WebSocketState.Open })
                await ws.SendAsync(new ArraySegment<byte>(data), messageType, true, token);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task<ControlMsg?> ReceiveControlAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        var result = await ws!.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        if (result.MessageType != WebSocketMessageType.Text)
            return null;
        return ControlMsg.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    private void Cleanup()
    {
        cts?.Cancel();
        try { sendLoop?.Wait(1000); } catch { }
        try { audioLoop?.Wait(1000); } catch { }
        cts?.Dispose();
        cts = null;
        sendLoop = null;
        audioLoop = null;

        encoder?.Dispose();
        encoder = null;

        opusEncoder?.Dispose();
        opusEncoder = null;

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

        IsHosting = false;
        IsLive = false;
    }

    public void Dispose()
    {
        Cleanup();
    }
}

// Stateful linear resampler for interleaved stereo int16 PCM.  Good enough
// for upsampling emulator audio (typically 32 kHz) to Opus's 48 kHz.
internal sealed class StereoResampler
{
    private readonly double step; // input frames per output frame
    private double pos;           // fractional position, relative to the current chunk

    public StereoResampler(int srcRate, int dstRate)
    {
        step = (double)srcRate / dstRate;
    }

    // Resamples src (srcFrames interleaved stereo frames) into dst.
    // Returns the number of output frames written.
    public int Resample(ReadOnlySpan<short> src, int srcFrames, Span<short> dst, int maxDstFrames)
    {
        var o = 0;

        // pos + 1 < srcFrames keeps one lookahead sample for interpolation.
        while (pos + 1 < srcFrames && o < maxDstFrames)
        {
            var i = (int)pos;
            var frac = pos - i;
            var a = i * 2;
            var b = a + 2;
            dst[o * 2] = (short)(src[a] + (src[b] - src[a]) * frac);
            dst[o * 2 + 1] = (short)(src[a + 1] + (src[b + 1] - src[a + 1]) * frac);
            o++;
            pos += step;
        }

        pos -= srcFrames;
        return o;
    }
}
