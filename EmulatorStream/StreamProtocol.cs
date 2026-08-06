using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmulatorStream;

// Wire protocol shared between host, relay, and spectator.
//
// Binary messages: [1-byte type][payload]
//   0x01 = H.264 access unit (one video frame, Annex B)
//   0x02 = Audio chunk (int16 stereo PCM, little-endian) — legacy hosts
//   0x03 = Stream info (UTF-8 JSON: width, height, fps, sample_rate, audio_codec)
//   0x04 = Netplay lockstep input (8 bytes, routed within netplay rooms only)
//   0x05 = Opus audio chunk: repeated [2-byte LE packet length][Opus packet]
//
// Text messages: JSON control plane (see relay/server.py for the full table).
public static class StreamProtocol
{
    public const byte MsgVideo = 0x01;
    public const byte MsgAudio = 0x02;
    public const byte MsgStreamInfo = 0x03;
    public const byte MsgAudioOpus = 0x05;

    public const string AudioCodecPcm = "pcm";
    public const string AudioCodecOpus = "opus";

    public static byte[] PackVideo(ReadOnlySpan<byte> h264)
    {
        var buf = new byte[1 + h264.Length];
        buf[0] = MsgVideo;
        h264.CopyTo(buf.AsSpan(1));
        return buf;
    }

    public static byte[] PackAudio(ReadOnlySpan<byte> pcm)
    {
        var buf = new byte[1 + pcm.Length];
        buf[0] = MsgAudio;
        pcm.CopyTo(buf.AsSpan(1));
        return buf;
    }

    // Payload is already TLV-framed: [2-byte LE length][Opus packet] ...
    public static byte[] PackAudioOpus(ReadOnlySpan<byte> tlv)
    {
        var buf = new byte[1 + tlv.Length];
        buf[0] = MsgAudioOpus;
        tlv.CopyTo(buf.AsSpan(1));
        return buf;
    }

    public static byte[] PackStreamInfo(int width, int height, double fps, int sampleRate, string audioCodec)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StreamInfoMsg
        {
            Width = width,
            Height = height,
            Fps = fps,
            SampleRate = sampleRate,
            AudioCodec = audioCodec,
        });
        var buf = new byte[1 + json.Length];
        buf[0] = MsgStreamInfo;
        json.CopyTo(buf.AsSpan(1));
        return buf;
    }

    public static StreamInfoMsg? ParseStreamInfo(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<StreamInfoMsg>(payload);
    }
}

public sealed class StreamInfoMsg
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public double Fps { get; set; }

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; }

    // "opus" or "pcm".  Absent on legacy hosts — treat as "pcm".
    [JsonPropertyName("audio_codec")]
    public string? AudioCodec { get; set; }
}

// JSON control messages (text WebSocket frames).
public sealed class ControlMsg
{
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("room")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Room { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    // Netplay fields.
    [JsonPropertyName("uid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uid { get; set; }

    // Live streaming fields: the short public player ID ("1234-5678").
    [JsonPropertyName("player_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlayerId { get; set; }

    [JsonPropertyName("slot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Slot { get; set; }

    [JsonPropertyName("players")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Collections.Generic.List<NetplayPlayerInfo>? Players { get; set; }

    // Live streaming fields.
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("keys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Collections.Generic.List<string>? Keys { get; set; }

    [JsonPropertyName("live")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Collections.Generic.List<LivePlayerInfo>? Live { get; set; }

    // Presence fields.
    [JsonPropertyName("online")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Collections.Generic.List<OnlinePlayerInfo>? Online { get; set; }

    [JsonPropertyName("subscribers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Subscribers { get; set; }

    // The live host owns the physical world-screen placement. The relay
    // retains and forwards this state so every spectator uses the same
    // position, orientation, and width, including after joining mid-stream.
    [JsonPropertyName("screen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldScreenState? Screen { get; set; }

    public static ControlMsg? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<ControlMsg>(json); }
        catch { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

public sealed class NetplayPlayerInfo
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("slot")]
    public int Slot { get; set; }
}

public sealed class LivePlayerInfo
{
    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("viewers")]
    public int Viewers { get; set; }
}

public sealed class OnlinePlayerInfo
{
    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("live")]
    public bool Live { get; set; }
}

public sealed class WorldScreenState
{
    // Center position [x, y, z] followed by the outward normal [nx, ny, nz].
    // A null position means the host has not placed a world screen.
    [JsonPropertyName("position")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? Position { get; set; }

    [JsonPropertyName("width")]
    public float Width { get; set; }

    // Display aspect (width / height) of the host's current game video.
    // Absent/zero means the legacy 3:2 default; old relays strip unknown
    // fields, so viewers degrade to that default gracefully.
    [JsonPropertyName("aspect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Aspect { get; set; }

    public WorldScreenState Clone() => new()
    {
        Position = Position is null ? null : (float[])Position.Clone(),
        Width = Width,
        Aspect = Aspect,
    };
}
