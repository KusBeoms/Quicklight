using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Quicklight;

/// <summary>
/// Global hotkey. Uses RegisterHotKey; if another program already owns the combination, falls back to a low-level
/// keyboard hook that swallows it (so Alt+Space does not also open the window system menu).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312, HotkeyId = 0x5154;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly IntPtr _hwnd;
    readonly Action _onPressed;
    readonly HwndSource _source;
    bool _registered;
    IntPtr _hook;
    LowLevelKeyboardProc? _hookProc;
    uint _mods, _vk;

    public string Mode => _registered ? "RegisterHotKey" : _hook != IntPtr.Zero ? "keyboard hook" : "none";

    public HotkeyManager(IntPtr hwnd, Action onPressed)
    {
        _hwnd = hwnd;
        _onPressed = onPressed;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public bool Register(string spec, out string? error)
    {
        error = null;
        if (!TryParse(spec, out _mods, out _vk)) { error = "형식을 알 수 없습니다 (예: Alt+Space)"; return false; }
        if (RegisterHotKey(_hwnd, HotkeyId, _mods | MOD_NOREPEAT, _vk)) { _registered = true; return true; }

        int err = Marshal.GetLastWin32Error();
        // 1409 = already registered by another app: take it over with a hook.
        _hookProc = HookProc;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook != IntPtr.Zero) return true;
        error = $"Win32 오류 {err}";
        return false;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onPressed();
        }
        return IntPtr.Zero;
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            uint vk = (uint)Marshal.ReadInt32(lParam);
            if (vk == _vk && CurrentMods() == _mods)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(_onPressed);
                return 1; // swallow
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static uint CurrentMods()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        uint m = 0;
        if (Down(0x12)) m |= MOD_ALT;
        if (Down(0x11)) m |= MOD_CONTROL;
        if (Down(0x10)) m |= MOD_SHIFT;
        if (Down(0x5B) || Down(0x5C)) m |= MOD_WIN;
        return m;
    }

    internal static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                case "space": vk = 0x20; break;
                case "enter": vk = 0x0D; break;
                case "tab": vk = 0x09; break;
                case "`" or "oem3": vk = 0xC0; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var k when k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24: vk = (uint)(0x6F + f); break;
                default: return false;
            }
        }
        return vk != 0 && mods != 0;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
