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
    // Analog snapshot for cores that plug in RETRO_DEVICE_ANALOG (PS1/PS2).
    // Sticks in libretro convention: X positive right, Y positive DOWN.
    private short analogLeftX, analogLeftY, analogRightX, analogRightY;
    // Trigger pressure, 0..32767 (reported via RETRO_DEVICE_INDEX_ANALOG_BUTTON).
    private short analogL2, analogR2;
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
                analogLeftX = analogLeftY = analogRightX = analogRightY = 0;
                analogL2 = analogR2 = 0;
            }

            return;
        }

        var kb = config.InputMode != InputMode.Controller ? ReadKeyboard() : (ushort)0;
        var gp = config.InputMode != InputMode.Keyboard ? ReadGamepad() : (ushort)0;
        var state = (ushort)(kb | gp);
        escapeRequested = gamepadReader.Down(GamepadReader.LeftThumb) && gamepadReader.Down(GamepadReader.RightThumb);

        // Analog sticks + trigger pressure for analog cores. The digital
        // RetroPad above stays answered either way.
        var kbUse = config.InputMode != InputMode.Controller;
        var gpUse = config.InputMode != InputMode.Keyboard;
        var (lx, ly) = BlendAnalog(
            kbUse ? ReadKeyboardStick() : (0f, 0f),
            gpUse ? ApplyDeadzone(gamepadReader.LeftStickX, -gamepadReader.LeftStickY) : (0f, 0f));
        var (rx, ry) = gpUse
            ? ApplyDeadzone(gamepadReader.RightStickX, -gamepadReader.RightStickY)
            : (0f, 0f);

        var l2 = gpUse ? gamepadReader.LeftTriggerValue : 0f;
        var r2 = gpUse ? gamepadReader.RightTriggerValue : 0f;
        if (kbUse)
        {
            // Keys pull the triggers fully (the standard keyboard path).
            if (config.KeyBindings.TryGetValue(nameof(SnesButton.L2), out var l2Key) && KeyDown(l2Key))
                l2 = 1f;
            if (config.KeyBindings.TryGetValue(nameof(SnesButton.R2), out var r2Key) && KeyDown(r2Key))
                r2 = 1f;
        }

        lock (stateLock)
        {
            joypad = state;
            analogLeftX = AxisToRetro(lx);
            analogLeftY = AxisToRetro(ly);
            analogRightX = AxisToRetro(rx);
            analogRightY = AxisToRetro(ry);
            analogL2 = (short)Math.Clamp(Math.Round(l2 * 32767f), 0, 32767);
            analogR2 = (short)Math.Clamp(Math.Round(r2 * 32767f), 0, 32767);
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
        if (device == Libretro.DeviceJoypad && id <= 15)
        {
            lock (stateLock)
            {
                var state = port == (uint)LocalPort ? joypad : remoteJoypad;
                return (short)((state >> (int)id) & 1);
            }
        }

        if (device == Libretro.DeviceAnalog)
        {
            // Netplay syncs the digital joypad only; the remote port has no
            // analog source.
            if (port != (uint)LocalPort)
            {
                return 0;
            }

            lock (stateLock)
            {
                return index switch
                {
                    Libretro.AnalogIndexLeft => id switch
                    {
                        Libretro.AnalogIdX => analogLeftX,
                        Libretro.AnalogIdY => analogLeftY,
                        _ => (short)0,
                    },
                    Libretro.AnalogIndexRight => id switch
                    {
                        Libretro.AnalogIdX => analogRightX,
                        Libretro.AnalogIdY => analogRightY,
                        _ => (short)0,
                    },
                    // Trigger pressure (L2 on X, R2 on Y).
                    Libretro.AnalogIndexButton => id switch
                    {
                        Libretro.AnalogIdX => analogL2,
                        Libretro.AnalogIdY => analogR2,
                        _ => (short)0,
                    },
                    _ => 0,
                };
            }
        }

        return 0;
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

        // Directions come from the physical D-pad and the left stick (SNES-era
        // fallback for pads without a usable D-pad); both are digital here.
        if (gamepadReader.Down(GamepadReader.DPadUp)) state |= Bit(Libretro.JoypadUp);
        if (gamepadReader.Down(GamepadReader.DPadDown)) state |= Bit(Libretro.JoypadDown);
        if (gamepadReader.Down(GamepadReader.DPadLeft)) state |= Bit(Libretro.JoypadLeft);
        if (gamepadReader.Down(GamepadReader.DPadRight)) state |= Bit(Libretro.JoypadRight);

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

    // The four direction keys drive the left analog stick at full deflection
    // (screen coordinates: +Y down, diagonals normalized) — the standard
    // keyboard-analog mapping.
    private (float X, float Y) ReadKeyboardStick()
    {
        var x = 0f;
        var y = 0f;
        if (config.KeyBindings.TryGetValue(nameof(SnesButton.Left), out var left) && KeyDown(left)) x -= 1f;
        if (config.KeyBindings.TryGetValue(nameof(SnesButton.Right), out var right) && KeyDown(right)) x += 1f;
        if (config.KeyBindings.TryGetValue(nameof(SnesButton.Up), out var up) && KeyDown(up)) y -= 1f;
        if (config.KeyBindings.TryGetValue(nameof(SnesButton.Down), out var down) && KeyDown(down)) y += 1f;

        if (x != 0f && y != 0f)
        {
            x *= 0.70710678f;
            y *= 0.70710678f;
        }

        return (x, y);
    }

    private const float AnalogDeadzone = 0.15f;

    // Radial deadzone: kills rest drift, then rescales so deflection starts
    // at 0 right outside the deadzone instead of jumping.
    private static (float X, float Y) ApplyDeadzone(float x, float y)
    {
        var magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude < AnalogDeadzone)
        {
            return (0f, 0f);
        }

        var scale = Math.Min((magnitude - AnalogDeadzone) / (1f - AnalogDeadzone), 1f) / magnitude;
        return (x * scale, y * scale);
    }

    // Keyboard + stick add together (clamped), so holding a direction key
    // while nudging the stick never cancels out.
    private static (float X, float Y) BlendAnalog((float X, float Y) a, (float X, float Y) b)
    {
        var x = a.X + b.X;
        var y = a.Y + b.Y;
        var magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude > 1f)
        {
            x /= magnitude;
            y /= magnitude;
        }

        return (x, y);
    }

    private static short AxisToRetro(float value) =>
        (short)Math.Clamp(Math.Round(value * 32767f), -32768f, 32767f);

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
