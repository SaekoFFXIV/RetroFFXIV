using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RetroXIV;

// Reads gamepad input via three backends:
//   1. XInput — Xbox / XInput-mode controllers.
//   2. Windows Gaming Input on a dedicated STA thread.
//   3. Raw HID on a dedicated background thread with the HID parser API
//      (HidP_*) — auto-discovers button/axis layout, works with any controller.
public sealed class GamepadReader : IDisposable
{
    public const ushort DPadUp = 0x0001;
    public const ushort DPadDown = 0x0002;
    public const ushort DPadLeft = 0x0004;
    public const ushort DPadRight = 0x0008;
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;
    // Synthetic flags: analog triggers thresholded to digital (PS1's L2/R2).
    public const ushort LeftTrigger = 0x0400;
    public const ushort RightTrigger = 0x0800;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    // ── XInput ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct XIG { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XIS { public uint Pkt; public XIG G; }
    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int i, out XIS s);

    // ── WGI STA thread ──────────────────────────────────────────────
    private volatile bool wgiOk;
    private volatile ushort wgiBtns;
    private volatile float wgiSX, wgiSY;
    private volatile float wgiRX, wgiRY;
    private volatile float wgiLT, wgiRT;
    private volatile string wgiDbg = "STA starting";
    private volatile bool wgiRun = true;

    private static ushort MapWgi(Windows.Gaming.Input.GamepadButtons b)
    {
        ushort r = 0;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.A)) r |= A;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.B)) r |= B;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.X)) r |= X;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.Y)) r |= Y;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadUp)) r |= DPadUp;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadDown)) r |= DPadDown;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadLeft)) r |= DPadLeft;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.DPadRight)) r |= DPadRight;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.LeftShoulder)) r |= LeftShoulder;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.RightShoulder)) r |= RightShoulder;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.Menu)) r |= Start;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.View)) r |= Back;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.LeftThumbstick)) r |= LeftThumb;
        if (b.HasFlag(Windows.Gaming.Input.GamepadButtons.RightThumbstick)) r |= RightThumb;
        return r;
    }

    private void WgiLoop()
    {
        Windows.Gaming.Input.Gamepad? pad = null;
        while (wgiRun)
        {
            try
            {
                pad ??= Windows.Gaming.Input.Gamepad.Gamepads.Count > 0
                    ? Windows.Gaming.Input.Gamepad.Gamepads[0] : null;
                if (pad != null)
                {
                    var r = pad.GetCurrentReading();
                    var btns = MapWgi(r.Buttons);
                    if (r.LeftTrigger > TriggerThreshold) btns |= LeftTrigger;
                    if (r.RightTrigger > TriggerThreshold) btns |= RightTrigger;
                    wgiBtns = btns;
                    wgiSX = (float)r.LeftThumbstickX;
                    wgiSY = (float)r.LeftThumbstickY;
                    wgiRX = (float)r.RightThumbstickX;
                    wgiRY = (float)r.RightThumbstickY;
                    wgiLT = (float)r.LeftTrigger;
                    wgiRT = (float)r.RightTrigger;
                    wgiOk = true;
                    wgiDbg = $"WGI ok 0x{(int)r.Buttons:X4}";
                }
                else { wgiOk = false; wgiDbg = "WGI: 0 pads"; }
            }
            catch (Exception ex) { wgiOk = false; wgiDbg = $"WGI err: {ex.Message}"; pad = null; }
            Thread.Sleep(8);
        }
    }

    // ── Raw HID with HID parser API ─────────────────────────────────

    // HID P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttr { public int Size; public ushort Vid, Pid, Ver; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SpIfData { public int cbSize; public Guid Guid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SpIfDetail { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Path; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;           // BOOLEAN = 1 byte
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage, LinkUsagePage;
        public byte IsRange;           // BOOLEAN
        public byte IsStringRange;     // BOOLEAN
        public byte IsDesignatorRange; // BOOLEAN
        public byte IsAbsolute;        // BOOLEAN
        public byte HasNull;           // BOOLEAN
        public byte Reserved;
        public ushort BitSize, ReportCount;
        public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax;
        public int PhysicalMin, PhysicalMax;
        // Union (Range / NotRange) — 16 bytes
        public ushort UsageMin, UsageMax;
        public ushort StringMin, StringMax;
        public ushort DesignatorMin, DesignatorMax;
        public ushort DataIndexMin, DataIndexMax;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(IntPtr h, ref HidAttr a);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr ppd);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr ppd);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr ppd, out HidpCaps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, [Out] HidpValueCaps[] caps, ref uint count, IntPtr ppd);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out int value, IntPtr ppd, byte[] report, int reportLen);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength, IntPtr ppd, byte[] report, int reportLen);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, int i, ref SpIfData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref SpIfData d, ref SpIfDetail det, int sz, out int req, IntPtr dd);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr CreateFile(string p, uint a, uint s, IntPtr sec, int c, int f, IntPtr t);
    [DllImport("kernel32.dll")] private static extern bool ReadFile(IntPtr h, byte[] b, uint n, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const int HidP_Input = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    // HID Usage Page 1 (Generic Desktop) usages:
    private const ushort UsageX = 0x30;
    private const ushort UsageY = 0x31;
    private const ushort UsageZ = 0x32;  // often right stick X
    private const ushort UsageRz = 0x35; // often right stick Y
    private const ushort UsageHat = 0x39;
    // HID Usage Page 9 (Button) — buttons are usage 1, 2, 3, ...

    private volatile bool hidOk;
    private volatile ushort hidBtns;
    private volatile float hidSX, hidSY;
    private volatile float hidRX, hidRY;
    private volatile string hidDbg = "HID thread starting";
    private volatile bool hidRun = true;
    private volatile int dbgRawX, dbgRawY, dbgHat;
    private volatile string dbgBtns = "";

    // Discovered axis info for normalization.
    private int xMin, xMax, yMin, yMax, zMin, zMax, rzMin, rzMax;

    private void HidLoop()
    {
        while (hidRun)
        {
            var (handle, ppd, reportLen) = OpenAndParse();
            if (handle == IntPtr.Zero)
            {
                hidDbg = "HID: searching...";
                hidOk = false;
                Thread.Sleep(1000);
                continue;
            }

            hidDbg = "HID: connected";
            var buf = new byte[reportLen];

            while (hidRun)
            {
                if (!ReadFile(handle, buf, (uint)buf.Length, out _, IntPtr.Zero))
                {
                    hidDbg = "HID: disconnected";
                    hidOk = false;
                    break;
                }

                ParseReport(ppd, buf, reportLen, out var btns, out var sx, out var sy,
                    out var rx, out var ry);
                hidBtns = btns;
                hidSX = sx;
                hidSY = sy;
                hidRX = rx;
                hidRY = ry;
                hidOk = true;
                hidDbg = $"HID 0x{btns:X4} s={sx:F2},{sy:F2} rawX={dbgRawX} rawY={dbgRawY} hat={dbgHat} btns=[{dbgBtns}] range={xMin}/{xMax},{yMin}/{yMax}";
            }

            HidD_FreePreparsedData(ppd);
            CloseHandle(handle);
        }
    }

    private (IntPtr handle, IntPtr ppd, int reportLen) OpenAndParse()
    {
        HidD_GetHidGuid(out var guid);
        var hDev = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (hDev == (IntPtr)(-1)) return (IntPtr.Zero, IntPtr.Zero, 0);

        try
        {
            for (var i = 0; ; i++)
            {
                var ifd = new SpIfData { cbSize = Marshal.SizeOf<SpIfData>() };
                if (!SetupDiEnumDeviceInterfaces(hDev, IntPtr.Zero, ref guid, i, ref ifd)) break;

                var det = new SpIfDetail { cbSize = IntPtr.Size == 8 ? 8 : 6 };
                if (!SetupDiGetDeviceInterfaceDetail(hDev, ref ifd, ref det, 512, out _, IntPtr.Zero)) continue;

                var h = CreateFile(det.Path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == (IntPtr)(-1)) continue;

                var attr = new HidAttr { Size = Marshal.SizeOf<HidAttr>() };
                if (!HidD_GetAttributes(h, ref attr) || attr.Vid != 0x057E || attr.Pid != 0x2009)
                {
                    CloseHandle(h);
                    continue;
                }

                if (!HidD_GetPreparsedData(h, out var ppd))
                {
                    CloseHandle(h);
                    continue;
                }

                if (HidP_GetCaps(ppd, out var caps) != HIDP_STATUS_SUCCESS)
                {
                    HidD_FreePreparsedData(ppd);
                    CloseHandle(h);
                    continue;
                }

                // Discover axis ranges for normalization.
                DiscoverAxisRanges(ppd);

                return (h, ppd, caps.InputReportByteLength);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(hDev); }

        return (IntPtr.Zero, IntPtr.Zero, 0);
    }

    private void DiscoverAxisRanges(IntPtr ppd)
    {
        xMin = -128; xMax = 127; yMin = -128; yMax = 127; // defaults
        zMin = -128; zMax = 127; rzMin = -128; rzMax = 127;

        var count = (uint)32;
        var caps = new HidpValueCaps[count];
        if (HidP_GetValueCaps(HidP_Input, caps, ref count, ppd) != HIDP_STATUS_SUCCESS) return;

        for (var i = 0; i < count; i++)
        {
            if (caps[i].UsagePage != 1) continue; // Generic Desktop
            if (caps[i].UsageMin == UsageX || caps[i].UsageMax == UsageX)
            {
                xMin = caps[i].LogicalMin;
                xMax = caps[i].LogicalMax;
            }
            if (caps[i].UsageMin == UsageY || caps[i].UsageMax == UsageY)
            {
                yMin = caps[i].LogicalMin;
                yMax = caps[i].LogicalMax;
            }
            if (caps[i].UsageMin == UsageZ || caps[i].UsageMax == UsageZ)
            {
                zMin = caps[i].LogicalMin;
                zMax = caps[i].LogicalMax;
            }
            if (caps[i].UsageMin == UsageRz || caps[i].UsageMax == UsageRz)
            {
                rzMin = caps[i].LogicalMin;
                rzMax = caps[i].LogicalMax;
            }
        }
    }

    private void ParseReport(IntPtr ppd, byte[] report, int len,
        out ushort btns, out float sx, out float sy, out float rx, out float ry)
    {
        btns = 0; sx = 0; sy = 0; rx = 0; ry = 0;

        // Read axes via HID parser.
        if (HidP_GetUsageValue(HidP_Input, 1, 0, UsageX, out var rawX, ppd, report, len) == HIDP_STATUS_SUCCESS)
        {
            dbgRawX = rawX;
            var range = xMax - xMin;
            sx = range > 0 ? (rawX - xMin) / (float)range * 2f - 1f : 0;
        }
        if (HidP_GetUsageValue(HidP_Input, 1, 0, UsageY, out var rawY, ppd, report, len) == HIDP_STATUS_SUCCESS)
        {
            dbgRawY = rawY;
            var range = yMax - yMin;
            sy = range > 0 ? -((rawY - yMin) / (float)range * 2f - 1f) : 0; // invert Y
        }

        // Right stick (Z / Rz on most generic pads).
        if (HidP_GetUsageValue(HidP_Input, 1, 0, UsageZ, out var rawZ, ppd, report, len) == HIDP_STATUS_SUCCESS)
        {
            var range = zMax - zMin;
            rx = range > 0 ? (rawZ - zMin) / (float)range * 2f - 1f : 0;
        }
        if (HidP_GetUsageValue(HidP_Input, 1, 0, UsageRz, out var rawRz, ppd, report, len) == HIDP_STATUS_SUCCESS)
        {
            var range = rzMax - rzMin;
            ry = range > 0 ? -((rawRz - rzMin) / (float)range * 2f - 1f) : 0; // invert Y
        }

        // Read hat switch.
        if (HidP_GetUsageValue(HidP_Input, 1, 0, UsageHat, out var hat, ppd, report, len) == HIDP_STATUS_SUCCESS)
        {
            dbgHat = hat;
            // Hat: 0=up, 1=up-right, ..., 7=up-left, 8 or max=centered
            switch (hat)
            {
                case 0: btns |= DPadUp; break;
                case 1: btns |= DPadUp | DPadRight; break;
                case 2: btns |= DPadRight; break;
                case 3: btns |= DPadRight | DPadDown; break;
                case 4: btns |= DPadDown; break;
                case 5: btns |= DPadDown | DPadLeft; break;
                case 6: btns |= DPadLeft; break;
                case 7: btns |= DPadLeft | DPadUp; break;
            }
        }

        // Read buttons via HidP_GetUsages (returns which button usages are pressed).
        ushort[] buttonMap = { 0, A, B, X, Y, LeftShoulder, RightShoulder, Back, Start, LeftThumb, RightThumb };
        var usageList = new ushort[32];
        var usageCount = (uint)usageList.Length;
        if (HidP_GetUsages(HidP_Input, 9, 0, usageList, ref usageCount, ppd, report, len) == HIDP_STATUS_SUCCESS)
        {
            var btnStr = "";
            for (var i = 0; i < usageCount; i++)
            {
                var u = usageList[i];
                btnStr += $"{u} ";
                // Standard gamepad button usage order: 1=A, 2=B, 3=X, 4=Y, 5=LB, 6=RB, 7=Back, 8=Start, 9=L3, 10=R3
                if (u >= 1 && u <= 10)
                    btns |= buttonMap[u];
            }
            dbgBtns = btnStr;
        }
    }

    // ── Unified state ───────────────────────────────────────────────

    private const double TriggerThreshold = 0.5;

    private ushort buttons;
    private float leftStickX, leftStickY;
    private float rightStickX, rightStickY;
    private float leftTrigger, rightTrigger;
    public bool Connected { get; private set; }
    public ushort Buttons => Connected ? buttons : (ushort)0;
    public string DebugInfo { get; private set; } = "not polled";
    // Short label of the backend currently providing state (for the UI).
    public string ActiveBackend { get; private set; } = "none";

    public GamepadReader()
    {
        var wgi = new Thread(WgiLoop) { IsBackground = true, Name = "RetroXIV.Wgi" };
        wgi.SetApartmentState(ApartmentState.STA);
        wgi.Start();

        var hid = new Thread(HidLoop) { IsBackground = true, Name = "RetroXIV.Hid" };
        hid.Start();
    }

    public void Poll()
    {
        for (var i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var xs) == 0)
            {
                Connected = true;
                var btns = xs.G.Buttons;
                if (xs.G.LT > TriggerThreshold * 255) btns |= LeftTrigger;
                if (xs.G.RT > TriggerThreshold * 255) btns |= RightTrigger;
                buttons = btns;
                leftStickX = xs.G.LX / 32768f;
                leftStickY = xs.G.LY / 32768f;
                rightStickX = xs.G.RX / 32768f;
                rightStickY = xs.G.RY / 32768f;
                leftTrigger = xs.G.LT / 255f;
                rightTrigger = xs.G.RT / 255f;
                DebugInfo = $"XInput slot {i}";
                ActiveBackend = $"XInput slot {i}";
                return;
            }
        }

        if (wgiOk)
        {
            Connected = true;
            buttons = wgiBtns;
            leftStickX = wgiSX;
            leftStickY = wgiSY;
            rightStickX = wgiRX;
            rightStickY = wgiRY;
            leftTrigger = wgiLT;
            rightTrigger = wgiRT;
            DebugInfo = wgiDbg;
            ActiveBackend = "Windows Gaming Input";
            return;
        }

        if (hidOk)
        {
            Connected = true;
            buttons = hidBtns;
            leftStickX = hidSX;
            leftStickY = hidSY;
            rightStickX = hidRX;
            rightStickY = hidRY;
            // The raw-HID fallback has no portable trigger mapping; the
            // digital threshold flags come through the button word instead.
            leftTrigger = 0;
            rightTrigger = 0;
            DebugInfo = hidDbg;
            ActiveBackend = "HID";
            return;
        }

        Connected = false;
        buttons = 0;
        leftStickX = 0;
        leftStickY = 0;
        rightStickX = 0;
        rightStickY = 0;
        leftTrigger = 0;
        rightTrigger = 0;
        DebugInfo = $"{wgiDbg} | {hidDbg}";
        ActiveBackend = "none";
    }

    public bool Down(ushort button) => Connected && (buttons & button) != 0;
    public float LeftStickX => Connected ? leftStickX : 0f;
    public float LeftStickY => Connected ? leftStickY : 0f;
    public float RightStickX => Connected ? rightStickX : 0f;
    public float RightStickY => Connected ? rightStickY : 0f;
    // Analog trigger pull, 0..1.
    public float LeftTriggerValue => Connected ? leftTrigger : 0f;
    public float RightTriggerValue => Connected ? rightTrigger : 0f;

    public void Dispose()
    {
        wgiRun = false;
        hidRun = false;
    }
}
