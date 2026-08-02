using System.Collections.Generic;
using EmulatorStream;

namespace SnesEmulator;

public enum SnesButton
{
    Up,
    Down,
    Left,
    Right,
    A,
    B,
    X,
    Y,
    L,
    R,
    Start,
    Select,
}

public enum InputMode
{
    Both,
    Keyboard,
    Controller,
}

public sealed class Configuration : Dalamud.Configuration.IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string CorePath { get; set; } = string.Empty;
    public string SelectedCorePath { get; set; } = string.Empty;
    public string RomDirectory { get; set; } = string.Empty;
    public int ResolutionScale { get; set; } = 3;
    public float Volume { get; set; } = 1.0f;
    public bool ShowFps { get; set; }
    public InputMode InputMode { get; set; } = InputMode.Both;

    // CRT effect toggles.
    public bool Scanlines { get; set; } = true;
    public bool ApertureGrille { get; set; } = true;
    public bool Vignette { get; set; } = true;
    public bool ScreenGlow { get; set; }

    // Streaming / netplay.
    public string RelayUrl { get; set; } = "wss://relay.nekomail.cc";
    public string PlayerUid { get; set; } = System.Guid.NewGuid().ToString("N");

    // XIVAuth identity.
    public string XivAuthAccessToken { get; set; } = string.Empty;
    public string XivAuthRefreshToken { get; set; } = string.Empty;
    public long XivAuthTokenExpiry { get; set; }
    public string PlayerPersistentKey { get; set; } = string.Empty;
    public string PlayerCharacterName { get; set; } = string.Empty;
    public long PlayerLodestoneId { get; set; }
    public string PlayerWorld { get; set; } = string.Empty;

    // World screen placement.
    public float ScreenWidth { get; set; } = 1.5f; // yalms
    public float ScreenHeight { get; set; } = 1.2f; // yalms above ground
    public float ScreenOpacity { get; set; } = 0.85f; // 0-1
    public float[]? ScreenPosition { get; set; }    // saved world position [x, y, z]

    // Build a StreamConfig snapshot for the shared streaming library.
    public StreamConfig GetStreamConfig() => new()
    {
        RelayUrl = RelayUrl,
        PlayerUid = PlayerUid,
        ScreenWidth = ScreenWidth,
        ScreenHeight = ScreenHeight,
        ScreenOpacity = ScreenOpacity,
        ScreenPosition = ScreenPosition,
    };

    public Dictionary<string, int> KeyBindings { get; set; } = DefaultKeyBindings();
    public Dictionary<string, int> ControllerBindings { get; set; } = DefaultControllerBindings();

    public static Dictionary<string, int> DefaultKeyBindings() => new()
    {
        [nameof(SnesButton.Up)] = 0x26,     // Arrow Up
        [nameof(SnesButton.Down)] = 0x28,   // Arrow Down
        [nameof(SnesButton.Left)] = 0x25,   // Arrow Left
        [nameof(SnesButton.Right)] = 0x27,  // Arrow Right
        [nameof(SnesButton.A)] = 0x58,      // X
        [nameof(SnesButton.B)] = 0x5A,      // Z
        [nameof(SnesButton.X)] = 0x53,      // S
        [nameof(SnesButton.Y)] = 0x41,      // A
        [nameof(SnesButton.L)] = 0x51,      // Q
        [nameof(SnesButton.R)] = 0x45,      // E
        [nameof(SnesButton.Start)] = 0x0D,  // Enter
        [nameof(SnesButton.Select)] = 0x10, // Shift
    };

    // Default controller mapping (XInput button flags), matched by physical position: SNES A/B/X/Y
    // map to the XInput face buttons in the same position (B/A/Y/X), shoulders to LB/RB.
    public static Dictionary<string, int> DefaultControllerBindings() => new()
    {
        [nameof(SnesButton.A)] = 0x2000,      // XInput B (right)
        [nameof(SnesButton.B)] = 0x1000,      // XInput A (bottom)
        [nameof(SnesButton.X)] = 0x8000,      // XInput Y (top)
        [nameof(SnesButton.Y)] = 0x4000,      // XInput X (left)
        [nameof(SnesButton.L)] = 0x0100,      // LB
        [nameof(SnesButton.R)] = 0x0200,      // RB
        [nameof(SnesButton.Start)] = 0x0010,  // Start
        [nameof(SnesButton.Select)] = 0x0020, // Back
    };
}
