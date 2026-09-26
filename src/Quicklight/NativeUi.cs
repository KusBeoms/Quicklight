using System.Runtime.InteropServices;

namespace Quicklight;

internal static class NativeUi
{
    /// <summary>Work area (physical pixels), DPI scale and refresh rate (Hz) of the monitor under the mouse cursor.</summary>
    public static (RECT Work, double Scale, int RefreshRate) MonitorUnderCursor()
    {
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(mon, ref info);
        double scale = GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        // 0 or 1 means "hardware default"; 60 is the safe guess then.
        int hz = EnumDisplaySettings(info.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, ref mode) && mode.dmDisplayFrequency > 1
            ? mode.dmDisplayFrequency : 60;
        return (info.rcWork, scale, hz);
    }

    public static void Move(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);

    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    public static IntPtr Foreground() => GetForegroundWindow();

    /// <summary>Types Ctrl+V into the foreground window.</summary>
    public static void SendPaste()
    {
        const byte VK_CONTROL = 0x11, VK_V = 0x56;
        const uint KEYUP = 2;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }

    /// <summary>The Explorer "Properties" dialog of a file or folder.</summary>
    public static bool ShowProperties(string path) => SHObjectProperties(IntPtr.Zero, 2 /* SHOP_FILEPATH */, path, null);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    /// <summary>
    /// Brings the window to the foreground even when another app has focus. SetForegroundWindow can report success
    /// without taking effect (foreground lock), so the result is checked and stronger fallbacks are tried in turn.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (IsForeground(hwnd)) return true;
        if (SetForegroundWindow(hwnd) && IsForeground(hwnd)) return true;

        // Share the input state of the current foreground thread, which is allowed to change the foreground.
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _), self = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { AttachThreadInput(self, fgThread, false); }
            if (IsForeground(hwnd)) return true;
        }

        // Last resort: a synthetic Alt tap satisfies the foreground lock rules.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        return IsForeground(hwnd);
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    /// <summary>
    /// Per-pixel transparency composed by DWM on the GPU. A WPF layered window (AllowsTransparency) instead copies
    /// every frame back to the CPU, which halves the frame rate of the show/hide animation.
    /// </summary>
    public static void MakeGpuTransparent(IntPtr hwnd)
    {
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int square = 1 /* DWMWCP_DONOTROUND */, noBorder = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */;
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref square, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));
    }

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
