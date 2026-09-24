using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quicklight.Core;

public sealed class QuicklightSettings
{
    /// <summary>Global hotkey, e.g. "Alt+Space", "Ctrl+Shift+Space", "Win+Alt+K".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>{0} is replaced by the URL-encoded query.</summary>
    public string WebSearchUrl { get; set; } = "https://www.google.com/search?q={0}";

    public int MaxResults { get; set; } = 12;

    /// <summary>Everything-backed file search.</summary>
    public bool FileSearch { get; set; } = true;

    /// <summary>At most this many files/folders among the results.</summary>
    public int MaxFileResults { get; set; } = 6;

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

    /// <summary>"hello 번역", "사과 영어로": translation with a local LibreTranslate server.</summary>
    public bool Translation { get; set; } = true;

    /// <summary>Local LibreTranslate server. Only localhost addresses are accepted, so text never leaves the PC.</summary>
    public string LibreTranslateUrl { get; set; } = "http://127.0.0.1:5055";

    /// <summary>LibreTranslate checkout with a .venv, started on demand. Empty = find ".library\LibreTranslate" near the exe.</summary>
    public string LibreTranslateDir { get; set; } = "";

    /// <summary>With no LibreTranslate found, download and install one on the first translation (about 1 GB with models).</summary>
    public bool AutoInstallTranslation { get; set; } = true;

    /// <summary>File search: use Quicklight's bundled Everything when none is installed (installs its service once, with consent).</summary>
    public bool BundledEverything { get; set; } = true;

    /// <summary>"ask" until the user answered the one-time Everything setup prompt, then "installed" or "declined".</summary>
    public string EverythingSetup { get; set; } = "ask";

    /// <summary>GitHub repository ("owner/name") whose latest release "update" installs.</summary>
    public string UpdateRepository { get; set; } = "KusBeoms/Quicklight";

    /// <summary>Check for a newer release shortly after start and every few hours; install it while the launcher is closed.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Language models the server loads (and downloads on first start).</summary>
    public List<string> TranslationLanguages { get; set; } = ["ko", "en", "ja", "zh"];

    /// <summary>Target for text that is already in the system language ("사과 번역" → English).</summary>
    public string SecondaryLanguage { get; set; } = "en";

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
                return JsonSerializer.Deserialize<QuicklightSettings>(File.ReadAllText(path), JsonOptions) ?? new();
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

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
