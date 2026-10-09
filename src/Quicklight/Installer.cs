using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Quicklight.Core;
using Quicklight.Core.Update;

namespace Quicklight;

/// <summary>
/// First run of a downloaded exe: offers to install into Program Files (one UAC prompt), or to keep running where it is.
/// Existing installs (any folder) are remembered in HKCU so updates and later starts never ask again.
/// </summary>
public static class Installer
{
    const string KeyPath = @"Software\Quicklight";
    const string ValueName = "Location";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Quicklight";

    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Quicklight");
    static string InstalledExe => Path.Combine(InstallDir, Updater.ExeName);
    static string Self => Environment.ProcessPath!;

    static bool IsPublishedBuild => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a published exe that has never run from this path (not installed, not registered, not an update).</summary>
    public static bool ShouldOffer(string[] args)
    {
        if (!IsPublishedBuild || args.Contains("--updated")) return false;
        if (Same(Path.GetDirectoryName(Self), InstallDir)) return false;
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        // Autostart.IsEnabled: installs from before this registry value existed.
        return !Same(k?.GetValue(ValueName) as string, Self) && !Autostart.IsEnabled;
    }

    /// <summary>Remembers this location as an install, so the offer is not repeated.</summary>
    public static void Register()
    {
        try { Registry.CurrentUser.CreateSubKey(KeyPath).SetValue(ValueName, Self); }
        catch (Exception ex) { Log.Error("install registration failed", ex); }
    }

    /// <summary>Asks; returns true when this process should go on running (the user chose to stay where it is).</summary>
    public static bool OfferInstall()
    {
        var answer = MessageBox.Show(
            $"Quicklight을 설치할까요?\n\n설치 위치: {InstallDir}\n관리자 권한 확인(UAC)이 한 번 나옵니다.\n\n" +
            "[아니요]를 누르면 설치하지 않고 이 위치에서 그대로 실행합니다.",
            "Quicklight 설치", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes,
            MessageBoxOptions.DefaultDesktopOnly);
        if (answer == MessageBoxResult.No) return true;
        if (answer == MessageBoxResult.Yes && RunElevated())
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir })?.Dispose();
        else if (answer == MessageBoxResult.Yes)
            MessageBox.Show("설치하지 못했습니다.", "Quicklight 설치", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    static bool RunElevated()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Self, "--install") { UseShellExecute = true, Verb = "runas" });
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // UAC declined
    }

    /// <summary>
    /// "Quicklight.exe --install", run elevated: copies this exe into Program Files and lets every user modify that
    /// folder, so the updater (which replaces the exe in place) works without elevation.
    /// </summary>
    public static int InstallElevated()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            foreach (var p in Process.GetProcessesByName("Quicklight")) // a running copy would lock the target
                using (p)
                    if (p.Id != Environment.ProcessId && Same(p.MainModule?.FileName, InstalledExe)) { p.Kill(); p.WaitForExit(5000); }
            File.Copy(Self, InstalledExe, true);
            // "Apps > Installed apps" entry; no DisplayVersion, it would go stale with in-place updates.
            using (var u = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                u.SetValue("DisplayName", "Quicklight");
                u.SetValue("Publisher", "kusbeoms");
                u.SetValue("InstallLocation", InstallDir);
                u.SetValue("DisplayIcon", InstalledExe);
                u.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                u.SetValue("NoModify", 1, RegistryValueKind.DWord);
                u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            using var acl = Process.Start(new ProcessStartInfo("icacls", $"\"{InstallDir}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q") { UseShellExecute = false, CreateNoWindow = true });
            return acl is not null && acl.WaitForExit(30_000) && acl.ExitCode == 0 ? 0 : 2;
        }
        catch { return 1; } // elevated: no logging into the user profile (see EverythingBootstrap.InstallElevated)
    }

    /// <summary>"Quicklight.exe --uninstall": confirms, removes autostart, then removes the install elevated. Settings and history stay.</summary>
    public static int Uninstall()
    {
        if (MessageBox.Show("Quicklight을 제거할까요?\n설정과 기록은 남습니다.", "Quicklight 제거",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return 1;
        Autostart.Sync(false);
        try { Registry.CurrentUser.DeleteSubKey(KeyPath, false); } catch (Exception ex) { Log.Error("uninstall registration cleanup failed", ex); }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Self, "--uninstall-elevated") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(); // this process is stopped by the elevated one, which also needs to delete our exe
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return 1; } // UAC declined
        return 0;
    }

    /// <summary>Runs elevated: stops every Quicklight, drops the Apps entry and deletes the folder after this exe has exited.</summary>
    public static int UninstallElevated()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Quicklight"))
                using (p)
                    if (p.Id != Environment.ProcessId) { p.Kill(); p.WaitForExit(5000); }
            Registry.LocalMachine.DeleteSubKey(UninstallKey, false);
            // The running exe cannot delete itself: a detached cmd does it after a short wait.
            Process.Start(new ProcessStartInfo("cmd", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>
    /// A newer (or equal) Quicklight started from another folder replaces the ones still running from elsewhere.
    /// Returns true if any was stopped. Its autostart entry is the same Run value, which the new one overwrites.
    /// </summary>
    public static bool StopOtherInstalls()
    {
        bool any = false;
        var mine = Updater.CurrentVersion;
        foreach (var p in Process.GetProcessesByName("Quicklight"))
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (p.Id == Environment.ProcessId || path is null || Same(path, Self)) continue;
                    var v = FileVersionInfo.GetVersionInfo(path);
                    if (new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) > mine) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                    any = true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { } // not ours to stop
            }
        return any;
    }
}
