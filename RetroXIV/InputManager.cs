using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using System;
using System.Runtime.InteropServices;
using RetroXIV.Emulation;

namespace RetroXIV;

// Maps keyboard and gamepad input to SNES buttons and handles input capture. Input is split into two
// paths that run on different threads:
//   - UpdateInputForEmulator (emulation thread): reads the real input directly from Windows
//     (GetAsyncKeyState / XInput) so the emulator sees it regardless of suppression.
//   - SuppressGameInput (game thread): hides input from the game by zeroing its key array and
//     setting NavEnableGamepad.
// Because the emulator reads the hardware directly and the game reads its own (zeroed) state, the
// two are fully decoupled.
public sealed class InputManager
{
    private const float StickThreshold = 0.5f;

    private readonly Configuration config;
    private readonly IKeyState keyState;
    private readonly GamepadReader gamepadReader = new();
    private readonly object stateLock = new();

    private ushort joypad;
    private ushort remoteJoypad;
    private volatile bool escapeRequested;

    public bool EscapeRequested => escapeRequested;

    // Netplay: which port the local player controls (0 = host/P1, 1 = joiner/P2).
    public int LocalPort { get; set; }

    // Netplay: set the remote player's input (called from the emulation
    // thread after receiving it from the network).
    public void SetRemoteInput(ushort input)
    {
        lock (stateLock)
        {
            remoteJoypad = input;
        }
    }

    // Netplay: get the local player's packed joypad state for sending.
    public ushort GetLocalJoypad()
    {
        lock (stateLock)
        {
            return joypad;
        }
    }

    // Exposed so the UI can read/poll the gamepad for controller rebinding.
    public GamepadReader Gamepad => gamepadReader;

    public InputManager(Configuration config, IKeyState keyState)
    {
        this.config = config;
        this.keyState = keyState;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // UI thread: poll the gamepad here because WinRT (Windows Gaming Input)
    // requires an STA / UI thread.  The emulation thread reads the cached result.
    public void PollGamepad() => gamepadReader.Poll();

    // Emulation thread: sample the real input for the emulator, once per frame.
    public void UpdateInputForEmulator(bool focused)
    {
        if (!focused)
        {
            escapeRequested = false;
            lock (stateLock)
            {
                joypad = 0;
            }

            return;
        }

        var kb = config.InputMode != InputMode.Controller ? ReadKeyboard() : (ushort)0;
        var gp = config.InputMode != InputMode.Keyboard ? ReadGamepad() : (ushort)0;
        var state = (ushort)(kb | gp);
        escapeRequested = gamepadReader.Down(GamepadReader.LeftThumb) && gamepadReader.Down(GamepadReader.RightThumb);
        lock (stateLock)
        {
            joypad = state;
        }
    }

    // Game thread: hide keyboard input from the game while the emulator window is focused.
    // Gamepad input cannot be suppressed at the application level (the game polls XInput
    // directly); disable it in FFXIV's settings instead.
    public void SuppressGameInput(bool focused)
    {
        if (!focused)
            return;

        foreach (var (name, vk) in config.KeyBindings)
        {
            if (keyState.IsVirtualKeyValid(vk))
            {
                keyState[vk] = false;
            }
        }
    }

    // Called by the core (emulation thread) for each input query.
    // In netplay, LocalPort determines which port the local player controls;
    // the other port reads the remote player's networked input.
    public short GetInputState(uint port, uint device, uint index, uint id)
    {
        if (device != Libretro.DeviceJoypad || id > 15)
        {
            return 0;
        }

        lock (stateLock)
        {
            var state = port == (uint)LocalPort ? joypad : remoteJoypad;
            return (short)((state >> (int)id) & 1);
        }
    }

    private ushort ReadKeyboard()
    {
        ushort state = 0;
        foreach (var (name, vk) in config.KeyBindings)
        {
            if (!Enum.TryParse<SnesButton>(name, out var button))
            {
                continue;
            }

            if (KeyDown(vk))
            {
                state |= SnesBit(button);
            }
        }

        return state;
    }

    private ushort ReadGamepad()
    {
        ushort state = 0;

        // D-Pad on the left stick (fixed).
        var stickX = gamepadReader.LeftStickX;
        var stickY = gamepadReader.LeftStickY;
        if (stickX < -StickThreshold) state |= Bit(Libretro.JoypadLeft);
        if (stickX > StickThreshold) state |= Bit(Libretro.JoypadRight);
        if (stickY > StickThreshold) state |= Bit(Libretro.JoypadUp);
        if (stickY < -StickThreshold) state |= Bit(Libretro.JoypadDown);

        // Buttons from the configurable controller mapping.
        foreach (var (name, flag) in config.ControllerBindings)
        {
            if (!Enum.TryParse<SnesButton>(name, out var button))
            {
                continue;
            }

            if (gamepadReader.Down((ushort)flag))
            {
                state |= SnesBit(button);
            }
        }

        return state;
    }

    private static ushort Bit(uint joypadId) => (ushort)(1 << (int)joypadId);

    private static ushort SnesBit(SnesButton button) => button switch
    {
        SnesButton.B => Bit(Libretro.JoypadB),
        SnesButton.Y => Bit(Libretro.JoypadY),
        SnesButton.Select => Bit(Libretro.JoypadSelect),
        SnesButton.Start => Bit(Libretro.JoypadStart),
        SnesButton.Up => Bit(Libretro.JoypadUp),
        SnesButton.Down => Bit(Libretro.JoypadDown),
        SnesButton.Left => Bit(Libretro.JoypadLeft),
        SnesButton.Right => Bit(Libretro.JoypadRight),
        SnesButton.A => Bit(Libretro.JoypadA),
        SnesButton.X => Bit(Libretro.JoypadX),
        SnesButton.L => Bit(Libretro.JoypadL),
        SnesButton.R => Bit(Libretro.JoypadR),
        SnesButton.L2 => Bit(Libretro.JoypadL2),
        SnesButton.R2 => Bit(Libretro.JoypadR2),
        _ => 0,
    };
}
