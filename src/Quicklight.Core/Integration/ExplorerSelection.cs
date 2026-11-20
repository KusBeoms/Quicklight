namespace Quicklight.Core.Integration;

/// <summary>The files selected in the File Explorer window that was in front, for "ask about these".</summary>
public static class ExplorerSelection
{
    /// <summary>
    /// Paths selected in the Explorer window <paramref name="foregroundHwnd"/>; empty when it is not Explorer or on any
    /// failure. Never throws. Runs on its own short-lived STA thread because the shell's COM objects need one.
    /// </summary>
    public static IReadOnlyList<string> GetSelectedPaths(IntPtr foregroundHwnd)
    {
        var result = new List<string>();
        var thread = new Thread(() =>
        {
            try { Read(foregroundHwnd, result); }
            catch (Exception ex) { Log.Error("explorer selection failed", ex); } // COM throws all kinds of things
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A hung Explorer must not hang the launcher: give up (the thread is a background thread and just ends later).
        return thread.Join(TimeSpan.FromSeconds(2)) ? result.ToList() : [];
    }

    static void Read(IntPtr hwnd, List<string> result)
    {
        if (hwnd == IntPtr.Zero) return;
        // Late-bound: no COM reference to add or ship. Windows() lists every Explorer window and, on Windows 11, every tab.
        // ponytail: tabs of one window share its HWND, so the first tab that has a selection wins, not necessarily the active one.
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                if ((long)window.HWND != hwnd.ToInt64()) continue;
                var paths = new List<string>();
                foreach (dynamic item in window.Document.SelectedItems()) paths.Add((string)item.Path);
                if (paths.Count == 0) continue;
                result.AddRange(paths);
                return;
            }
            catch (Exception) { } // a window that is not a folder view (Internet Explorer mode, a closing tab)
        }
    }
}
