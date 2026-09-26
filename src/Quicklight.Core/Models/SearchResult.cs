namespace Quicklight.Core.Models;

public enum ResultKind
{
    Calculator,
    Currency,
    Url,
    Path,
    App,
    Setting,
    Command,
    Folder,
    File,
    WebSearch,
    Window,
    Clipboard,
    Snippet,
    Process,
}

public enum ActionType
{
    /// <summary>ShellExecute the target (file, folder, app, URL, ms-settings: URI).</summary>
    Open,
    /// <summary>Copy <see cref="SearchResult.Target"/> to the clipboard.</summary>
    Copy,
    /// <summary>Run a built-in system command identified by <see cref="SearchResult.Target"/>.</summary>
    System,
    /// <summary>Informational row: Enter does nothing.</summary>
    None,
    /// <summary>Check GitHub Releases and install a newer Quicklight (handled by the launcher).</summary>
    Update,
    /// <summary>Re-runs the search including the system files/folders it left out (handled by the launcher).</summary>
    Expand,
    /// <summary>Put <see cref="SearchResult.Target"/> (or the clipboard history item) on the clipboard and paste it into the previous window (handled by the launcher).</summary>
    Paste,
    /// <summary>Bring the top-level window whose handle is <see cref="SearchResult.Target"/> to the front.</summary>
    SwitchWindow,
    /// <summary>End every process named <see cref="SearchResult.Target"/>.</summary>
    Kill,
}

public sealed class SearchResult
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public required ResultKind Kind { get; init; }
    public required string Target { get; init; }
    public ActionType Action { get; init; } = ActionType.Open;

    /// <summary>Command-line arguments for <see cref="ActionType.Open"/> targets that are programs.</summary>
    public string? Arguments { get; init; }

    /// <summary>Path or shell parsing name used to fetch the shell icon; null means the kind's glyph.</summary>
    public string? IconSource { get; init; }

    /// <summary>Stable identity for dedupe and usage learning.</summary>
    public string Key => $"{Kind switch { ResultKind.Folder or ResultKind.File or ResultKind.Path => "fs", _ => Kind.ToString() }}:{Target.ToLowerInvariant()}"
                         + (Arguments is null ? "" : " " + Arguments); // same program, different arguments: different results

    /// <summary>Destructive commands ask for a second Enter.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>File system path to reveal in Explorer, when there is one.</summary>
    public string? RevealPath { get; init; }

    public double Score { get; set; }

    /// <summary>Files and folders: the query matched the name itself, not only somewhere in the path.</summary>
    public bool NameMatch { get; init; }

    /// <summary>A file or folder with Windows' hidden or system attribute (what Explorer calls "system files").</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// The "시스템 폴더"/"시스템 파일" placeholder row standing in for system files/folders left out of the results;
    /// Enter on it (<see cref="ActionType.Expand"/>) reruns the search with them included. Always sorts last.
    /// </summary>
    public bool IsSystemSummary { get; init; }

    /// <summary>Apps: the whole app (shortcuts, executable, installer, uninstaller), for the preview and the context menu.</summary>
    public Shell.AppEntry? App { get; init; }

    public DateTime? Modified { get; init; }
    public long? Size { get; init; }

    public override string ToString() => $"[{Kind} {Score:0.0}] {Title} — {Subtitle}";
}
