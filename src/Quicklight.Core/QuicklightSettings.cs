using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quicklight.Core;

/// <param name="Path">Program, script or document to open.</param>
/// <param name="Arguments">Command-line arguments; null for none.</param>
public sealed record CustomCommand(string Name, string Path, string? Arguments = null, string[]? Aliases = null);

public sealed class QuicklightSettings
{
    /// <summary>Global hotkey, e.g. "Alt+Space", "Ctrl+Shift+Space", "Win+Alt+K".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>
    /// Also open with the 한/영 key held and the hotkey's key pressed ("Hangul+Space" for "Alt+Space"). The hotkey itself
    /// keeps working. Off by default: it takes the 한/영 key into a keyboard hook (see HangulCombo).
    /// </summary>
    public bool HangulHotkey { get; set; }

    /// <summary>{0} is replaced by the URL-encoded query.</summary>
    public string WebSearchUrl { get; set; } = "https://www.google.com/search?q={0}";

    /// <summary>"yt 고양이" searches with the engine keyed "yt". {0} is replaced by the URL-encoded rest of the query.</summary>
    public Dictionary<string, string> SearchEngines { get; set; } = new()
    {
        ["g"] = "https://www.google.com/search?q={0}",
        ["yt"] = "https://www.youtube.com/results?search_query={0}",
        ["nv"] = "https://search.naver.com/search.naver?query={0}",
        ["w"] = "https://ko.wikipedia.org/w/index.php?search={0}",
    };

    /// <summary>Text snippets: key -> text. Typing the key (or part of the text) and Enter pastes the text.</summary>
    public Dictionary<string, string> Snippets { get; set; } = [];

    /// <summary>User-defined commands: a name (and aliases) that runs a program with arguments.</summary>
    public List<CustomCommand> Commands { get; set; } = [];

    /// <summary>
    /// Use the result counts and path lists below. Off: the built-in values are used instead, and these stay as they
    /// are for when it is switched on again.
    /// </summary>
    public bool AdvancedSearch { get; set; } = true;

    static readonly QuicklightSettings Builtin = new();

    [JsonIgnore] public int ResultLimit => AdvancedSearch ? MaxResults : Builtin.MaxResults;
    [JsonIgnore] public int FileResultLimit => AdvancedSearch ? MaxFileResults : Builtin.MaxFileResults;
    [JsonIgnore] public int FolderResultLimit => AdvancedSearch ? MaxFolderResults : Builtin.MaxFolderResults;
    [JsonIgnore] public IReadOnlyList<string> DemotedPathsInEffect => AdvancedSearch ? DemotedPaths : Builtin.DemotedPaths;
    [JsonIgnore] public IReadOnlyList<string> ExcludedPathsInEffect => AdvancedSearch ? ExcludedPaths : Builtin.ExcludedPaths;

    public int MaxResults { get; set; } = 12;

    /// <summary>Everything-backed file search.</summary>
    public bool FileSearch { get; set; } = true;

    /// <summary>At most this many files among the results.</summary>
    public int MaxFileResults { get; set; } = 6;

    /// <summary>
    /// At most this many folders among the results. Folders whose name matches keep up to 3 of these slots even
    /// when apps and files score higher.
    /// </summary>
    public int MaxFolderResults { get; set; } = 4;

    /// <summary>Paths containing any of these fragments rank lower (not hidden).</summary>
    public List<string> DemotedPaths { get; set; } =
    [
        @"\AppData\", @"\$Recycle.Bin\", @"\Windows\", @"\node_modules\", @"\.git\", @"\obj\", @"\bin\Debug\", @"\bin\Release\",
        @"\ProgramData\", @"\.nuget\", @"\.cache\", @"\site-packages\", @"\__pycache__\", @"\WinSxS\",
    ];

    /// <summary>Paths containing any of these fragments are never shown.</summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>"100달러", "5만원 엔": currency conversion with daily rates (only the rate table is downloaded).</summary>
    public bool CurrencyConversion { get; set; } = true;

    /// <summary>
    /// ISO code conversions go to when none is given. "auto" = the currency of the Windows region (KRW in Korea);
    /// amounts already in that currency go to USD.
    /// </summary>
    public string DefaultCurrency { get; set; } = "auto";

    /// <summary>File search: use Quicklight's bundled Everything when none is installed (installs its service once, with consent).</summary>
    public bool BundledEverything { get; set; } = true;

    /// <summary>"ask" until the user answered the one-time Everything setup prompt, then "installed" or "declined".</summary>
    public string EverythingSetup { get; set; } = "ask";

    /// <summary>GitHub repository ("owner/name") whose latest release "update" installs.</summary>
    public string UpdateRepository { get; set; } = "KusBeoms/Quicklight";

    /// <summary>Check for a newer release shortly after start and every few hours; install it while the launcher is closed.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Commands typed in the search box (a program on PATH, a cmd built-in) can run in a mini terminal inside the launcher.</summary>
    public bool Terminal { get; set; }

    public bool LaunchAtStartup { get; set; } = true;

    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>"acrylic" (blurred view of what is behind the window) or "solid".</summary>
    public string Backdrop { get; set; } = "acrylic";

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quicklight");
    public static string DefaultPath => Path.Combine(Directory, "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Loads settings, writing defaults if the file does not exist. A broken file falls back to defaults without overwriting it.</summary>
    public static QuicklightSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<QuicklightSettings>(File.ReadAllText(path), JsonOptions) ?? new();
                loaded.MoveHangulOutOfHotkey();
                return loaded;
            }
            var s = new QuicklightSettings();
            s.Save(path);
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error("settings load failed", ex);
            return new QuicklightSettings();
        }
    }

    static bool IsHangulToken(string part) => part.Equals("Hangul", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "Hangul+Alt+Space" (the 한/영 key inside the hotkey, how an earlier build wrote it) becomes the hotkey "Alt+Space"
    /// with <see cref="HangulHotkey"/> on. Without a modifier of its own left ("Hangul+Space") the hotkey gets Alt.
    /// </summary>
    internal void MoveHangulOutOfHotkey()
    {
        var parts = (Hotkey ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(IsHangulToken)) return;
        HangulHotkey = true;
        var rest = parts.Where(p => !IsHangulToken(p)).ToList();
        bool modifier = rest.Any(p => p.ToLowerInvariant() is "alt" or "ctrl" or "control" or "shift" or "win" or "windows");
        Hotkey = rest.Count == 0 ? "Alt+Space" : string.Join("+", modifier ? rest : rest.Prepend("Alt"));
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
