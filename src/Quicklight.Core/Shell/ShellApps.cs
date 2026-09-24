using System.Runtime.InteropServices;
using Quicklight.Core.Native;

namespace Quicklight.Core.Shell;

/// <param name="Name">Display name as the Start menu shows it.</param>
/// <param name="ParsingName">AUMID for packaged apps or a (known-folder) path for desktop apps.</param>
/// <param name="LinkTarget">Target of the Start menu shortcut behind the entry, when the shell exposes one.</param>
public sealed record AppEntry(string Name, string ParsingName, string? LinkTarget = null)
{
    public string LaunchTarget => @"shell:AppsFolder\" + ParsingName;

    /// <summary>
    /// File system path: from the parsing name ("C:\...\app.exe" or "{known folder}\...\app.exe") or else the shortcut target.
    /// Null for packaged (Store) apps and web links.
    /// </summary>
    public string? FilePath { get; } = ResolvePath(ParsingName) ?? (IsLocalPath(LinkTarget) ? LinkTarget : null);

    static bool IsLocalPath(string? p) => p is { Length: > 3 } && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
        ["{905e63b6-c1bf-494e-b29c-65b732d3d21a}"] = Environment.SpecialFolder.ProgramFiles,
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.SpecialFolder.SystemX86,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
        ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.SpecialFolder.LocalApplicationData,
        ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = Environment.SpecialFolder.ApplicationData,
    };

    static string? ResolvePath(string parsing)
    {
        if (parsing.Length > 2 && parsing[1] == ':') return parsing;
        // "{known folder GUID}\relative\path": the braced GUID is 38 characters.
        if (parsing.Length > 39 && parsing[0] == '{' && parsing[37] == '}' && parsing[38] == '\\' && KnownFolders.TryGetValue(parsing[..38], out var sf))
            return Path.Combine(Environment.GetFolderPath(sf), parsing[39..]);
        return null;
    }
}

public static class ShellApps
{
    /// <summary>
    /// Enumerates shell:AppsFolder, the same list the Start menu "All apps" shows: desktop shortcuts and Store apps.
    /// Runs on its own STA thread because shell folders expect apartment-threaded callers.
    /// </summary>
    public static Task<IReadOnlyList<AppEntry>> EnumerateAsync()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Enumerate()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "AppsFolder enum" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    static List<AppEntry> Enumerate()
    {
        var list = new List<AppEntry>();
        var folderId = Native.Shell.FOLDERID_AppsFolder;
        var iidItem = Native.Shell.IID_IShellItem;
        Native.Shell.SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out var folderObj);
        var folder = (Native.Shell.IShellItem)folderObj;
        var bhid = Native.Shell.BHID_EnumItems;
        var iidEnum = Native.Shell.IID_IEnumShellItems;
        if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var enumObj) != 0) return list;
        var e = (Native.Shell.IEnumShellItems)enumObj;
        var one = new Native.Shell.IShellItem[1];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (e.Next(1, one, out var fetched) == 0 && fetched == 1)
        {
            var item = one[0];
            var name = Native.Shell.GetName(item, Native.Shell.SIGDN.NORMALDISPLAY);
            var parsing = Native.Shell.GetName(item, Native.Shell.SIGDN.DESKTOPABSOLUTEPARSING);
            // Shortcut-based entries (hash ids, AUMIDs of desktop apps) carry their target as a shell property.
            var target = Native.Shell.GetStringProperty(item, Native.Shell.PKEY_Link_TargetParsingPath);
            Marshal.ReleaseComObject(item);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) continue;
            if (seen.Add(parsing)) list.Add(new AppEntry(name, parsing, target));
        }
        Marshal.ReleaseComObject(e);
        Marshal.ReleaseComObject(folder);
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
                    map.TryAdd(System.IO.Path.GetFileNameWithoutExtension(f), f);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return map;
    }
}
