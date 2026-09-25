using System.Diagnostics;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Native;

namespace Quicklight.Core.Providers;

/// <summary>Open windows, found by title or program name; Enter brings the window to the front (restoring it if minimized).</summary>
public sealed class WindowProvider : IResultProvider
{
    public string Name => "windows";

    /// <param name="Handle">HWND as a number.</param>
    public sealed record OpenWindow(long Handle, string Title, string ProcessName, string? ExePath);

    readonly object _gate = new();
    List<OpenWindow> _snapshot = [];
    long _snapshotAt = long.MinValue;

    // Off the UI thread: enumerating windows and their programs takes long enough to stutter typing.
    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct) =>
        Task.Run<IReadOnlyList<SearchResult>>(() => Match(query), ct);

    /// <summary>One enumeration serves every query stage of a keystroke and a burst of typing.</summary>
    List<OpenWindow> Snapshot()
    {
        lock (_gate)
        {
            if (Environment.TickCount64 - _snapshotAt > 1000) { _snapshot = List(); _snapshotAt = Environment.TickCount64; }
            return _snapshot;
        }
    }

    List<SearchResult> Match(QueryContext query)
    {
        var results = new List<SearchResult>();
        foreach (var w in Snapshot())
        {
            double m = Math.Max(FuzzyMatcher.Score(query.Text, w.Title), FuzzyMatcher.Score(query.Text, w.ProcessName) - 4);
            if (m < query.MinMatch) continue;
            results.Add(new SearchResult
            {
                Title = w.Title,
                Subtitle = $"열린 창 · {w.ProcessName}",
                Kind = ResultKind.Window,
                Target = w.Handle.ToString(),
                Action = ActionType.SwitchWindow,
                IconSource = w.ExePath,
                Score = Scores.WindowBase + m,
            });
        }
        return results;
    }

    /// <summary>Windows that appear in Alt+Tab, front to back, excluding Quicklight's own.</summary>
    public static List<OpenWindow> List()
    {
        var handles = new List<IntPtr>();
        Win32.EnumWindows((h, _) => { handles.Add(h); return true; }, IntPtr.Zero);

        var names = new Dictionary<uint, (string Name, string? Exe)>();
        var list = new List<OpenWindow>();
        foreach (var h in handles)
        {
            if (!IsAltTabWindow(h)) continue;
            int len = Win32.GetWindowTextLength(h);
            if (len == 0) continue;
            var buf = new char[len + 1];
            var title = new string(buf, 0, Win32.GetWindowText(h, buf, buf.Length));
            Win32.GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) continue;
            if (!names.TryGetValue(pid, out var info))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    string? exe = null;
                    try { exe = p.MainModule?.FileName; } catch { } // elevated processes deny access
                    info = (p.ProcessName, exe);
                }
                catch (ArgumentException) { continue; } // exited meanwhile
                names[pid] = info;
            }
            list.Add(new OpenWindow(h.ToInt64(), title, info.Name, info.Exe));
        }
        return list;
    }

    static bool IsAltTabWindow(IntPtr h)
    {
        if (!Win32.IsWindowVisible(h) || Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return false;
        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return false;
        // Suspended UWP frames and windows on other virtual desktops are "cloaked".
        return Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    /// <summary>Brings the window to the front, restoring it when minimized.</summary>
    public static void Activate(long handle)
    {
        var h = new IntPtr(handle);
        if (!Win32.IsWindow(h)) throw new InvalidOperationException("창이 이미 닫혔습니다.");
        if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(h);
    }
}
