using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quicklight.Core.Shell;

public static class ShellLauncher
{
    /// <summary>ShellExecute a file, folder, URL, ms-settings: URI or shell:AppsFolder item.</summary>
    public static void Open(string target, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (workingDirectory is not null && Directory.Exists(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        Process.Start(psi)?.Dispose();
    }

    /// <summary>A command line as the registry writes them ("\"C:\\App\\unins000.exe\" /x", "MsiExec.exe /X{...}") split into program and arguments.</summary>
    public static (string File, string Arguments) SplitCommandLine(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.StartsWith('"') && s.IndexOf('"', 1) is var end and > 0) return (s[1..end], s[(end + 1)..].Trim());
        if (File.Exists(s)) return (s, ""); // an unquoted path with spaces
        int exe = s.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? (s[..(exe + 4)], s[(exe + 5)..].Trim()) : (s, "");
    }

    /// <summary>Extensions that "Run as administrator" makes sense for.</summary>
    static readonly HashSet<string> ElevatableExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".bat", ".cmd", ".msc", ".msi" };

    public static bool CanRunAsAdmin(string? path) =>
        path is not null && ElevatableExtensions.Contains(Path.GetExtension(path)) && File.Exists(path);

    /// <summary>Starts the program elevated (UAC prompt). Returns false when the user declines the prompt.</summary>
    public static bool RunAsAdmin(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // cancelled at the UAC prompt
    }

    /// <summary>Opens Explorer with the item selected.</summary>
    public static void Reveal(string path)
    {
        if (Directory.Exists(path) && (path.EndsWith('\\') || path.EndsWith(':')))
        {
            Open(path);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    public static void RunSystemCommand(string id)
    {
        switch (id)
        {
            case "lock": LockWorkStation(); break;
            case "sleep": SetSuspendState(false, false, false); break;
            case "hibernate": SetSuspendState(true, false, false); break;
            case "signout": ExitWindowsEx(0 /* EWX_LOGOFF */, 0); break;
            case "restart": Shutdown("/r /t 0"); break;
            case "shutdown": Shutdown("/s /t 0"); break;
            case "recyclebin": SHEmptyRecycleBin(IntPtr.Zero, null, 0); break;
            default: throw new ArgumentException("Unknown system command: " + id);
        }
    }

    static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
}
