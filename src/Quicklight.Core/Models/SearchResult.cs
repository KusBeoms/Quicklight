namespace Quicklight.Core.Models;

public enum ResultKind
{
    Calculator,
    Translation,
    Currency,
    Url,
    Path,
    App,
    Setting,
    Command,
    Folder,
    File,
    WebSearch,
}

public enum ActionType
{
    /// <summary>ShellExecute the target (file, folder, app, URL, ms-settings: URI).</summary>
    Open,
    /// <summary>Copy <see cref="SearchResult.Target"/> to the clipboard.</summary>
    Copy,
    /// <summary>Run a built-in system command identified by <see cref="SearchResult.Target"/>.</summary>
    System,
    /// <summary>Informational row (e.g. "translating…"): Enter does nothing.</summary>
    None,
    /// <summary>Check GitHub Releases and install a newer Quicklight (handled by the launcher).</summary>
    Update,
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
    public string Key => $"{Kind switch { ResultKind.Folder or ResultKind.File or ResultKind.Path => "fs", _ => Kind.ToString() }}:{Target.ToLowerInvariant()}";

    /// <summary>Destructive commands ask for a second Enter.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>File system path to reveal in Explorer, when there is one.</summary>
    public string? RevealPath { get; init; }

    public double Score { get; set; }

    public DateTime? Modified { get; init; }
    public long? Size { get; init; }

    public override string ToString() => $"[{Kind} {Score:0.0}] {Title} — {Subtitle}";
}
