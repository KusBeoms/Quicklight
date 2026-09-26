using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Quicklight.Core.Shell;

/// <summary>
/// What a piece of an app is. The order is Enter's priority (the first one that still exists runs) and the preview's
/// order. Tools (updater, error reporter) and the uninstaller never run on Enter.
/// </summary>
public enum AppPartKind { Package, Shortcut, Exe, Installer, Tool, Uninstaller }

/// <param name="Target">AUMID for a package, the uninstall command line for an uninstaller, a file path otherwise.</param>
/// <param name="Arguments">Arguments of the shortcut an executable came from, used when the executable runs instead of it.</param>
public sealed record AppPart(AppPartKind Kind, string Target, string? Arguments = null)
{
    public string LaunchTarget => Kind == AppPartKind.Package ? @"shell:AppsFolder\" + Target : Target;

    public bool Exists => Kind is AppPartKind.Package or AppPartKind.Uninstaller || File.Exists(Target);

    /// <summary>What Enter on the app can run.</summary>
    public bool IsMain => Kind is not (AppPartKind.Tool or AppPartKind.Uninstaller);
}

/// <summary>One app with everything that belongs to it: shortcuts, executable, installer, uninstaller.</summary>
public sealed class AppEntry
{
    readonly List<AppPart> _parts = [];

    public string Name { get; }
    public IReadOnlyList<AppPart> Parts => _parts;
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Location { get; set; }

    public AppEntry(string name, IEnumerable<AppPart>? parts = null)
    {
        Name = name;
        foreach (var p in parts ?? []) Add(p);
    }

    /// <summary>Shorthand: a package id ("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"), a shortcut (.lnk) or an executable.</summary>
    public AppEntry(string name, string target) : this(name, [new AppPart(
        !target.Contains('\\') ? AppPartKind.Package
        : target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? AppPartKind.Shortcut : AppPartKind.Exe, target)]) { }

    internal void Add(AppPart part)
    {
        if (!_parts.Exists(p => p.Kind == part.Kind && string.Equals(p.Target, part.Target, StringComparison.OrdinalIgnoreCase))) _parts.Add(part);
    }

    public AppPart? Part(AppPartKind kind) => _parts.Find(p => p.Kind == kind);

    /// <summary>What Enter runs: package, shortcut, executable, installer, whichever comes first and still exists. Never a tool or the uninstaller.</summary>
    public AppPart Launch
    {
        get
        {
            var runnable = _parts.Where(p => p.IsMain).OrderBy(p => p.Kind).ToList();
            return runnable.Find(p => p.Exists) ?? runnable[0];
        }
    }

    public string LaunchTarget => Launch.LaunchTarget;

    public string? ExePath => Part(AppPartKind.Exe)?.Target;

    /// <summary>The executable (else the shortcut) on disk, for Explorer and properties. Null for packages.</summary>
    public string? FilePath => ExePath ?? Part(AppPartKind.Shortcut)?.Target;

    public string IconSource => Part(AppPartKind.Package)?.LaunchTarget ?? FilePath ?? Launch.Target;
}

/// <summary>
/// Quicklight's own app list, not Windows' "All apps": Start menu and desktop shortcuts, the uninstall entries in the
/// registry, installers in Downloads and Store packages, grouped so each app is one entry.
/// </summary>
public static class ShellApps
{
    internal enum Role { App, Installer, Tool, Uninstaller, Noise }

    /// <summary>Start menu entries that are not apps at all: help links, readmes, websites.</summary>
    static readonly string[] NoiseWords = ["readme", "read me", "documentation", "도움말", "help", "website", "웹 사이트", "release notes", "license", "manual"];
    static readonly string[] UninstallWords = ["uninstall", "제거"];
    static readonly string[] InstallWords = ["install", "setup", "설치"];
    /// <summary>Helpers that come with an app: "EA app 업데이터", "EA Error Reporter", "App Recovery".</summary>
    static readonly string[] ToolWords = ["updater", "update", "업데이터", "업데이트", "error report", "crash report", "오류 보고", "recovery", "복구", "repair"];

    internal static Role RoleOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (UninstallWords.Any(n.Contains)) return Role.Uninstaller; // before install: "uninstall" contains it
        if (NoiseWords.Any(n.Contains)) return Role.Noise;
        if (InstallWords.Any(n.Contains)) return Role.Installer;
        return ToolWords.Any(n.Contains) ? Role.Tool : Role.App;
    }

    static readonly HashSet<string> StemNoise =
    [
        "uninstall", "uninstaller", "install", "installer", "setup", "설치", "제거", "x64", "x86", "amd64", "arm64", "win64", "win32",
        "bit", "비트", "64비트", "32비트", "full", "offline", "online", "web", "user",
        "updater", "update", "업데이터", "업데이트", "error", "crash", "reporter", "report", "오류", "보고", "recovery", "복구", "repair",
        "app", "apps", "앱", "tool", "tools", "도구", // too common to tie anything to an app ("App Recovery" is not "NVIDIA App")
    ];

    /// <summary>
    /// A name's words without installer words and architectures: "Zoom Workplace (64-bit)" → [zoom, workplace].
    /// Versions go too unless <paramref name="keepVersions"/>. File names also split at humps ("ZoomInstallerFull" → [zoom]).
    /// </summary>
    internal static string[] Words(string name, bool keepVersions = false, bool fileName = false)
    {
        if (fileName) name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        var tokens = Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
        var words = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var w = tokens[i];
            if (StemNoise.Contains(w) || w is "64" or "32" && i + 1 < tokens.Count && tokens[i + 1] is "bit" or "비트") continue;
            if (keepVersions || !Regex.IsMatch(w, @"^v?\d+$")) words.Add(w);
        }
        return words.ToArray();
    }

    internal static string Stem(string name, bool fileName = false) => string.Concat(Words(name, fileName: fileName));

    /// <summary>Same app name: "Google Chrome" and "google-chrome". Versions count ("Python 3.11" ≠ "Python 3.12").</summary>
    static string Norm(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    internal sealed record Shortcut(string Path, string Name, string? Target, string? Arguments, bool OnDesktop);
    /// <param name="Runnable">
    /// <paramref name="Icon"/> is the program itself, on disk: not an uninstaller, installer or a file in the installer
    /// caches or Windows. Such an entry is an app even without a shortcut (Chrome after its shortcut was deleted).
    /// </param>
    internal sealed record Program(string Name, string? Publisher, string? Version, string? Location, string? Icon, string? Uninstall, bool Runnable = false);

    public static Task<List<AppEntry>> ScanAsync()
    {
        // Shortcuts are read through COM, which wants an apartment-threaded caller.
        var tcs = new TaskCompletionSource<List<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Group(ReadShortcuts(), ReadPrograms(), ReadInstallers(), ReadPackages())); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "App scan" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Groups the pieces into apps. Shortcuts make the apps (the same target or name twice is one app); uninstall
    /// and install shortcuts, registry entries and installer files join the apps they belong to.
    /// </summary>
    internal static List<AppEntry> Group(IEnumerable<Shortcut> shortcuts, IEnumerable<Program> programs, IEnumerable<string> installers, IEnumerable<AppEntry> packages)
    {
        var apps = new List<AppEntry>();
        var byName = new Dictionary<string, AppEntry>();
        var byTarget = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        AppEntry Named(string name)
        {
            var key = Norm(name);
            if (!byName.TryGetValue(key, out var app)) { apps.Add(app = new AppEntry(name)); byName[key] = app; }
            return app;
        }

        var extras = new List<(Shortcut Link, Role Role)>();
        foreach (var s in shortcuts.OrderBy(s => s.OnDesktop)) // Start menu names win over desktop ones
        {
            var role = RoleOf(s.Name);
            if (role == Role.Noise) continue;
            if (role != Role.App) { extras.Add((s, role)); continue; }
            bool exe = IsExe(s.Target);
            if (s.OnDesktop && !exe) continue; // desktop: shortcuts to programs, not to folders or documents
            var targetKey = exe ? s.Target + "|" + s.Arguments : null;
            if (targetKey is null || !byTarget.TryGetValue(targetKey, out var app)) app = Named(s.Name);
            if (targetKey is not null) byTarget.TryAdd(targetKey, app);
            app.Add(new AppPart(AppPartKind.Shortcut, s.Path));
            if (exe) app.Add(new AppPart(AppPartKind.Exe, s.Target!, string.IsNullOrEmpty(s.Arguments) ? null : s.Arguments));
        }
        foreach (var p in packages)
        {
            var app = Named(p.Name);
            foreach (var part in p.Parts) app.Add(part);
            app.Publisher ??= p.Publisher;
            app.Version ??= p.Version;
            app.Location ??= p.Location;
        }

        // ponytail: name matching by words is a heuristic ("VSCodeUserSetup" does not find "Visual Studio Code"); unmatched pieces are dropped.
        // Each app's words, worked out once (apps made from registry entries below join later).
        var words = new Dictionary<AppEntry, string[]>();
        var exact = new Dictionary<AppEntry, string>();
        string[] WordsOf(AppEntry a) => words.TryGetValue(a, out var w) ? w : words[a] = Words(a.Name);
        string ExactOf(AppEntry a) => exact.TryGetValue(a, out var e) ? e : exact[a] = string.Concat(Words(a.Name, keepVersions: true));
        // Loose, for uninstall/install shortcuts and installer files: "Uninstall Zoom" and "ZoomInstaller" belong to "Zoom Workplace".
        bool SameStem(AppEntry a, string name, bool fileName = false)
        {
            var stem = Stem(name, fileName);
            return stem.Length >= 3 && (string.Concat(WordsOf(a)) == stem || WordsOf(a).Contains(stem));
        }
        // Strict, for registry entries, which name whole products: versions count ("Python 3.12" is not IDLE for 3.11).
        bool SameProduct(AppEntry a, string name) => ExactOf(a).Length >= 3 && ExactOf(a) == string.Concat(Words(name, keepVersions: true));

        var primaries = apps.ToList();
        foreach (var (s, role) in extras)
        {
            var kind = role switch { Role.Uninstaller => AppPartKind.Uninstaller, Role.Tool => AppPartKind.Tool, _ => AppPartKind.Installer };
            var dir = IsExe(s.Target) ? Path.GetDirectoryName(s.Target) : null;
            var owners = primaries.Where(a => SameStem(a, s.Name) || dir is not null && a.ExePath is { } exe && IsUnder(exe, dir)).ToList();
            foreach (var a in owners) a.Add(new AppPart(kind, s.Path));
            // An installer or tool nobody claims is an app of its own ("Visual Studio Installer" with no Visual Studio).
            if (owners.Count == 0 && role != Role.Uninstaller) Named(s.Name).Add(new AppPart(AppPartKind.Shortcut, s.Path));
        }

        foreach (var p in programs)
        {
            var owners = primaries.Where(a => SameProduct(a, p.Name) || Owns(a, p)).ToList();
            if (owners.Count == 0 && p.Runnable && RoleOf(p.Name) == Role.App)
            {
                var app = Named(p.Name);
                app.Add(new AppPart(AppPartKind.Exe, p.Icon!));
                primaries.Add(app);
                owners.Add(app);
            }
            foreach (var a in owners)
            {
                if (!string.IsNullOrWhiteSpace(p.Uninstall)) a.Add(new AppPart(AppPartKind.Uninstaller, p.Uninstall.Trim()));
                a.Publisher ??= p.Publisher;
                a.Version ??= p.Version;
                a.Location ??= p.Location;
            }
        }

        foreach (var file in installers)
            foreach (var a in primaries.Where(a => SameStem(a, Path.GetFileNameWithoutExtension(file), fileName: true)))
                a.Add(new AppPart(AppPartKind.Installer, file));

        return apps;
    }

    /// <summary>
    /// The registry entry installed this app: its icon is the app's executable, or the executable lives in its install
    /// folder or next to (under) its uninstaller. Not for shortcuts with arguments: those are something else the
    /// program runs (Chrome web apps, profiles).
    /// </summary>
    static bool Owns(AppEntry a, Program p) =>
        a.Part(AppPartKind.Exe) is { Arguments: null, Target: var exe }
        && (p.Icon is not null && string.Equals(p.Icon, exe, StringComparison.OrdinalIgnoreCase)
            || p.Location is { Length: > 0 } loc && IsUnder(exe, loc)
            || p.Uninstall is not null && Path.GetDirectoryName(ShellLauncher.SplitCommandLine(p.Uninstall).File) is { Length: > 0 } dir && IsUnder(exe, dir));

    static bool IsExe(string? path) => path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders many apps share; being under one of them says nothing about which app a file belongs to.</summary>
    static readonly HashSet<string> SharedRoots = new(new[]
    {
        Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86, Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
    }.Select(Environment.GetFolderPath).Where(p => p.Length > 0)
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"))
        .Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);

    internal static bool IsUnder(string file, string dir)
    {
        dir = dir.Trim().Trim('"').TrimEnd('\\');
        if (dir.Length <= 3 || SharedRoots.Contains(dir)) return false;
        return file.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<Shortcut> ReadShortcuts()
    {
        var list = new List<Shortcut>();
        void Read(Environment.SpecialFolder folder, bool desktop)
        {
            var dir = Environment.GetFolderPath(folder);
            if (dir.Length == 0 || !Directory.Exists(dir)) return;
            var options = new EnumerationOptions { RecurseSubdirectories = !desktop, IgnoreInaccessible = true };
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", options))
            {
                var (target, args) = Native.Shell.ReadShortcut(lnk);
                list.Add(new Shortcut(lnk, Path.GetFileNameWithoutExtension(lnk), target, args, desktop));
            }
        }
        Read(Environment.SpecialFolder.CommonPrograms, false);
        Read(Environment.SpecialFolder.Programs, false);
        Read(Environment.SpecialFolder.CommonDesktopDirectory, true);
        Read(Environment.SpecialFolder.DesktopDirectory, true);
        return list;
    }

    /// <summary>Installed programs from the uninstall keys (machine 64/32-bit and user), as Settings > Apps lists them.</summary>
    static List<Program> ReadPrograms()
    {
        var list = new List<Program>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var k = uninstall.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string display || display.Length == 0) continue;
                    if (k.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") is string) continue; // updates and hidden parts
                    string? S(string v) => k.GetValue(v) is string s && s.Trim().Length > 0 ? s.Trim() : null;
                    // DisplayIcon: "C:\App\app.exe,0" or "\"C:\App\app.exe\"".
                    var icon = S("DisplayIcon")?.Split(',')[0].Trim('"');
                    icon = IsExe(icon) ? icon : null;
                    list.Add(new Program(display, S("Publisher"), S("DisplayVersion"), S("InstallLocation")?.Trim('"'), icon, S("UninstallString"), IsProgram(icon)));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { Log.Error("uninstall registry", ex); }
        }
        return list;
    }

    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>An executable that is an app itself, not a setup, uninstall or cached installer file, nor part of Windows.</summary>
    static bool IsProgram(string? exe) =>
        exe is not null && File.Exists(exe)
        && RoleOf(Path.GetFileNameWithoutExtension(exe)) == Role.App
        && !Path.GetFileName(exe).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileNameWithoutExtension(exe).EndsWith("inst", StringComparison.OrdinalIgnoreCase) // oalinst, dpinst
        && !exe.Contains(@"\DIFX\", StringComparison.OrdinalIgnoreCase) // driver packages
        && !exe.StartsWith(WindowsDir + "\\", StringComparison.OrdinalIgnoreCase)
        && !exe.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
        && !exe.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Installers lying in Downloads (top level): .msi files and .exe files named like setup/install.</summary>
    static List<string> ReadInstallers()
    {
        var dir = Native.Shell.DownloadsFolder();
        if (dir is null || !Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { IgnoreInaccessible = true })
            .Where(f => f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                        || IsExe(f) && RoleOf(Path.GetFileNameWithoutExtension(f)) == Role.Installer)
            .ToList();
    }

    /// <summary>Store (packaged) apps of the current user, one entry per Start menu tile.</summary>
    static List<AppEntry> ReadPackages()
    {
        var list = new List<AppEntry>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return list; // GetAppListEntries; older Windows goes without Store apps
        IEnumerable<Windows.ApplicationModel.Package> packages;
        try { packages = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""); }
        catch (Exception ex) { Log.Error("package list", ex); return list; }
        foreach (var pkg in packages)
        {
            try
            {
                if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;
                var v = pkg.Id.Version;
                foreach (var entry in pkg.GetAppListEntries())
                {
                    var name = entry.DisplayInfo.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new AppEntry(name, [new AppPart(AppPartKind.Package, entry.AppUserModelId)])
                    {
                        Publisher = pkg.PublisherDisplayName,
                        Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        Location = pkg.InstalledLocation?.Path,
                    });
                }
            }
            catch (Exception ex) { Log.Error("package " + pkg.Id.FullName, ex); } // half-installed or broken package
        }
        return list;
    }

    /// <summary>Executables on PATH by lower-case name without extension, e.g. "regedit" -> C:\Windows\regedit.exe.</summary>
    public static Dictionary<string, string> ScanPathExecutables()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in dirs)
        {
            string dir;
            try { dir = Environment.ExpandEnvironmentVariables(raw); if (!Directory.Exists(dir)) continue; }
            catch { continue; }
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.exe"))
                    map.TryAdd(Path.GetFileNameWithoutExtension(f), f);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return map;
    }
}
