using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Quicklight.Core;

namespace Quicklight;

/// <summary>
/// Global hotkey. Uses RegisterHotKey; if another program already owns the combination, falls back to a low-level
/// keyboard hook that swallows it (so Alt+Space does not also open the window system menu).
/// "Hangul+Space" (the 한/영 key as a modifier) cannot be registered at all, so it always uses the hook; see <see cref="HangulCombo"/>.
/// The hook runs on a thread of its own: Windows holds every keystroke of the whole system until the hook answers, and
/// silently removes a hook that stays slow (about a second), so it must never wait for a busy UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312, HotkeyId = 0x5154;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const uint MOD_HANGUL = 0x10000; // not a Windows modifier (they fit in 16 bits): the 한/영 key, which RegisterHotKey cannot take
    const int LLKHF_INJECTED = 0x10;

    readonly IntPtr _hwnd;
    readonly Action _onPressed;
    readonly HwndSource _source;
    readonly Dispatcher _ui;
    bool _registered;
    IntPtr _hook;
    LowLevelKeyboardProc? _hookProc;
    Thread? _hookThread;
    uint _hookThreadId;
    int _pressPending;
    uint _mods, _vk;
    HangulCombo? _combo;

    bool _primaryInHook; // RegisterHotKey was refused (another program has the combination): the hook answers for it
    int _hookError;

    public string Mode => (_registered ? "RegisterHotKey" : _primaryInHook && _hook != IntPtr.Zero ? "keyboard hook" : "none") + (_combo is not null ? " + 한/영 키" : "");

    public HotkeyManager(IntPtr hwnd, Action onPressed)
    {
        _hwnd = hwnd;
        _onPressed = onPressed;
        _ui = Dispatcher.CurrentDispatcher;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    /// <param name="hangul">Also take "Hangul+<the hotkey's key>" (한/영 키 held, then the key) as the hotkey; the hotkey itself keeps working.</param>
    public bool Register(string spec, bool hangul, out string? error)
    {
        error = null;
        if (!TryParse(spec, out _mods, out _vk)) { error = "형식을 알 수 없습니다 (예: Alt+Space)"; return false; }
        if ((_mods & MOD_HANGUL) != 0) { hangul = true; _mods &= ~MOD_HANGUL; } // an old "Hangul+Alt+Space": the 한/영 key is its own switch now
        if (_mods == 0) { error = "수정 키(Alt, Ctrl 등)가 필요합니다 (예: Alt+Space)"; return false; }

        _registered = RegisterHotKey(_hwnd, HotkeyId, _mods | MOD_NOREPEAT, _vk);
        int err = _registered ? 0 : Marshal.GetLastWin32Error();
        _primaryInHook = !_registered; // 1409 = already registered by another app: take it over with a hook
        if (_registered && !hangul) return true;

        StopHookThread(); // never two hooks: the hotkey would toggle twice (open and close at once)
        _hookProc = HookProc;
        _combo = hangul ? new HangulCombo(_vk, () => CurrentMods() == 0, (scan, down) => keybd_event((byte)HangulCombo.Hangul, (byte)scan, down ? 0u : 2u, UIntPtr.Zero), Press) : null;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            using (var module = Process.GetCurrentProcess().MainModule)
                _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(module?.ModuleName), 0);
            if (_hook == IntPtr.Zero) _hookError = Marshal.GetLastWin32Error();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */); // make sure the queue exists before a WM_QUIT is posted to it
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            // The hook is called through this thread's message loop.
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "Hotkey hook" };
        _hookThread.Start();
        ready.Wait();
        if (_hook != IntPtr.Zero) return true;
        _hookThread = null;
        _combo = null;
        error = hangul ? $"한/영 키 단축키를 만들지 못했습니다 (Win32 오류 {_hookError})" : $"Win32 오류 {(err != 0 ? err : _hookError)}";
        return false;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
        StopHookThread();
    }

    /// <summary>Ends the hook thread's message loop; the thread unhooks on its way out.</summary>
    void StopHookThread()
    {
        if (_hookThread is not { } thread) return;
        PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(2)) && _hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); // never leave it behind
        _combo?.Reset();
        _combo = null;
        _hookThread = null;
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
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN, up = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;
        // Injected keys (the 한/영 key sent on again by the combo, other programs' input) are never ours to take.
        if (nCode >= 0 && (down || up) && (Marshal.ReadInt32(lParam, 8) & LLKHF_INJECTED) == 0)
        {
            uint vk = (uint)Marshal.ReadInt32(lParam);
            bool swallow = false;
            try
            {
                if (_combo is { } combo && combo.Handle(vk, (uint)Marshal.ReadInt32(lParam, 4), down)) swallow = true; // 한/영 + key
                else if (_primaryInHook && down && vk == _vk && CurrentMods() == _mods) { Press(); swallow = true; }
            }
            catch { swallow = false; } // whatever goes wrong, the key goes through
            if (swallow) return 1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>The hotkey was pressed. Presses made while the UI thread is busy count once: a queue of toggles would open and close again.</summary>
    void Press()
    {
        if (Interlocked.Exchange(ref _pressPending, 1) == 0)
            _ui.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _pressPending, 0);
                _onPressed();
            });
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
                case "hangul": mods |= MOD_HANGUL; break;
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
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
}
