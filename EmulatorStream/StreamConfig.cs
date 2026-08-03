namespace EmulatorStream;

/// <summary>
/// Streaming/world-screen configuration.  Owned by the emulator plugin's
/// config class but passed to shared-lib components so they don't depend
/// on emulator-specific config types.
/// </summary>
public sealed class StreamConfig
{
    public string RelayUrl { get; set; } = "wss://relay.nekomail.cc";
    public string PlayerUid { get; set; } = System.Guid.NewGuid().ToString("N");
    public float ScreenWidth { get; set; } = 1.5f;
    public float ScreenHeight { get; set; } = 1.2f;
    public float ScreenOpacity { get; set; } = 0.85f;
}
