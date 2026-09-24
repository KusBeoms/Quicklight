using System.Diagnostics;
using Microsoft.Win32;

namespace Quicklight.Core.Everything;

/// <summary>
/// Makes file search work without a separate Everything install. Quicklight carries Everything (MIT, voidtools) inside:
/// <list type="number">
/// <item>An Everything that is already running or installed is used as is.</item>
/// <item>Otherwise, once and with the user's consent (UAC), an elevated Quicklight writes its bundled copy to
/// Program Files\Quicklight\Everything and registers the Everything service, which needs admin rights to read the
/// NTFS index. Program Files, not the user profile: a service binary in a user-writable folder could be swapped
/// by any program of that user to gain SYSTEM rights.</item>
/// <item>The Everything client from that folder is then started in the background whenever Quicklight starts.</item>
/// </list>
/// </summary>
public static class EverythingBootstrap
{
    const string ResourceName = "Quicklight.Everything.exe";
    const string LicenseResourceName = "Quicklight.Everything.License.txt";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Quicklight", "Everything");

    public static string InstalledExe => Path.Combine(InstallDir, "Everything.exe");

    /// <summary>An Everything installed by its own installer, or by Quicklight earlier.</summary>
    public static string? FindInstalled()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything"),
                     InstallDir,
                 })
        {
            var exe = Path.Combine(dir, "Everything.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static bool ServiceInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Everything");
        return key is not null;
    }

    public static bool HasBundledCopy => typeof(EverythingBootstrap).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    public enum Outcome { AlreadyRunning, Started, NeedsSetup, Disabled, Failed }

    /// <summary>Starts an installed Everything in the background if none is running. Never prompts.</summary>
    public static async Task<Outcome> EnsureRunningAsync(QuicklightSettings settings)
    {
        if (!settings.FileSearch) return Outcome.Disabled;
        if (EverythingClient.IsAvailable) return Outcome.AlreadyRunning;
        var exe = FindInstalled();
        if (exe is null) return HasBundledCopy ? Outcome.NeedsSetup : Outcome.Failed;
        try
        {
            // -startup: start in the background (tray) without opening the search window.
            Process.Start(new ProcessStartInfo(exe, "-startup") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("could not start Everything", ex);
            return Outcome.Failed;
        }
        for (int i = 0; i < 40 && !EverythingClient.IsAvailable; i++) await Task.Delay(250).ConfigureAwait(false);
        return EverythingClient.IsAvailable ? Outcome.Started : Outcome.Failed;
    }

    /// <summary>
    /// Asks for elevation (UAC) and runs "Quicklight.exe --install-everything" as admin. Returns false if the user
    /// declined or the install failed.
    /// </summary>
    public static async Task<bool> RequestInstallAsync(string quicklightExe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(quicklightExe, "--install-everything") { UseShellExecute = true, Verb = "runas" });
            if (p is null) return false;
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // declined
    }

    /// <summary>
    /// Runs elevated. Writes the bundled Everything from this assembly's own resources (never from a file a
    /// non-admin process could have replaced) to Program Files and registers its service.
    /// </summary>
    public static int InstallElevated()
    {
        // Elevated: write nothing outside Program Files (see catch below).
        try
        {
            Directory.CreateDirectory(InstallDir);
            WriteResource(ResourceName, InstalledExe);
            WriteResource(LicenseResourceName, Path.Combine(InstallDir, "License.txt"));
            using var p = Process.Start(new ProcessStartInfo(InstalledExe, "-install-service") { UseShellExecute = false, CreateNoWindow = true });
            if (p is null || !p.WaitForExit(60_000)) return 2;
            return ServiceInstalled() ? 0 : 3;
        }
        catch
        {
            // No logging here: this process is elevated and the log lives in the user's writable profile,
            // where a link could redirect an admin write. The exit code tells the launcher what happened.
            return 1;
        }
    }

    internal static void WriteResource(string name, string path)
    {
        using var src = typeof(EverythingBootstrap).Assembly.GetManifestResourceStream(name)
                        ?? throw new InvalidOperationException("missing resource " + name);
        using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    internal static byte[] ReadResource(string name)
    {
        using var src = typeof(EverythingBootstrap).Assembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }
}
