using System.Collections.Generic;
using EmulatorStream;

namespace RetroXIV;

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
    // Extra RetroPad shoulders (ids 12/13) — used by PS1 games; the SNES
    // never asks for them.
    L2,
    R2,
    // Stick clicks (ids 14/15) — PS2-era buttons. Default unbound so they
    // cannot collide with custom Start/Select mappings on the same keys.
    L3,
    R3,
    Start,
    Select,
    // Keyboard-only analog targets for PS1/PS2: which keys drive the sticks.
    // They carry no RetroPad bit of their own.
    LeftStickUp,
    LeftStickDown,
    LeftStickLeft,
    LeftStickRight,
    RightStickUp,
    RightStickDown,
    RightStickLeft,
    RightStickRight,
}

public enum InputMode
{
    Both,
    Keyboard,
    Controller,
}

public sealed class SyncFriend
{
    // Player ID ("K7QX-4MRT") — the public handle friends exchange.  Older
    // configs may still hold raw XIVAuth keys; those entries need re-adding.
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
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

    // The public player ID friends exchange ("K7QX-4MRT").  Issued by the
    // relay on registration and tied to the XIVAuth identity in PlayerIdUid;
    // empty until the player registers.
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerIdUid { get; set; } = string.Empty;

    // XIVAuth identity.
    public string XivAuthAccessToken { get; set; } = string.Empty;
    public string XivAuthRefreshToken { get; set; } = string.Empty;
    public long XivAuthTokenExpiry { get; set; }
    public string PlayerPersistentKey { get; set; } = string.Empty;
    public string PlayerCharacterName { get; set; } = string.Empty;
    public long PlayerLodestoneId { get; set; }
    public string PlayerWorld { get; set; } = string.Empty;

    // Sync friends (XIVAuth persistent_keys + display names).
    public List<SyncFriend> SyncFriends { get; set; } = new();

    // World screen placement.
    public float ScreenWidth { get; set; } = 1.5f; // yalms
    // The local broadcast screen is opt-in. Placement remains available while it is hidden.
    public bool ShowLocalWorldScreen { get; set; }
    public float ScreenHeight { get; set; } = 1.2f; // yalms above ground
    public float ScreenOpacity { get; set; } = 0.85f; // 0-1
    // [x, y, z], followed by the outward surface normal [nx, ny, nz] for
    // object-mounted screens. Three-value legacy placements are accepted and
    // migrated to a fixed upright orientation the next time they render.
    public float[]? ScreenPosition { get; set; }

    // DX11 depth-integrated world screens are the normal rendering path.
    public bool UseDxWorldScreen { get; set; } = true;

    // Saved world positions for watched streams, keyed by player ID.
    public Dictionary<string, float[]> WatchScreenPositions { get; set; } = new();

    // Build a StreamConfig snapshot for the shared streaming library.
    public StreamConfig GetStreamConfig() => new()
    {
        RelayUrl = RelayUrl,
        PlayerUid = !string.IsNullOrEmpty(PlayerPersistentKey) ? PlayerPersistentKey : PlayerUid,
        ScreenWidth = ScreenWidth,
        ScreenHeight = ScreenHeight,
        // Opacity is intentionally fixed while the UI has no opacity control.
        ScreenOpacity = 1f,
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
        [nameof(SnesButton.L2)] = 0x31,     // 1
        [nameof(SnesButton.R2)] = 0x32,     // 2
        [nameof(SnesButton.L3)] = 0,        // unbound by default
        [nameof(SnesButton.R3)] = 0,        // unbound by default
        [nameof(SnesButton.Start)] = 0x0D,  // Enter
        [nameof(SnesButton.Select)] = 0x10, // Shift
        // Analog sticks (PS1/PS2): arrows mirror the D-pad by default,
        // IJKL drives the right stick.
        [nameof(SnesButton.LeftStickUp)] = 0x26,     // Arrow Up
        [nameof(SnesButton.LeftStickDown)] = 0x28,   // Arrow Down
        [nameof(SnesButton.LeftStickLeft)] = 0x25,   // Arrow Left
        [nameof(SnesButton.LeftStickRight)] = 0x27,  // Arrow Right
        [nameof(SnesButton.RightStickUp)] = 0x49,    // I
        [nameof(SnesButton.RightStickDown)] = 0x4B,  // K
        [nameof(SnesButton.RightStickLeft)] = 0x4A,  // J
        [nameof(SnesButton.RightStickRight)] = 0x4C, // L
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
        [nameof(SnesButton.L2)] = 0x0400,     // LT (analog trigger, thresholded)
        [nameof(SnesButton.R2)] = 0x0800,     // RT (analog trigger, thresholded)
        [nameof(SnesButton.L3)] = 0,          // unbound: many players map Start/Select
        [nameof(SnesButton.R3)] = 0,          // to the stick clicks instead
        [nameof(SnesButton.Start)] = 0x0010,  // Start
        [nameof(SnesButton.Select)] = 0x0020, // Back
    };
}
