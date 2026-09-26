using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Shell;

namespace Quicklight.Core.Providers;

/// <summary>Quicklight's own app list (see <see cref="ShellApps"/>, kept in SQLite) plus exact-name executables on PATH ("regedit", "cmd").</summary>
public sealed class AppProvider : IResultProvider
{
    volatile IReadOnlyList<AppEntry> _apps = [];
    volatile IReadOnlyDictionary<string, AppEntry> _owners = new Dictionary<string, AppEntry>();
    volatile IReadOnlyDictionary<string, string> _pathExes = new Dictionary<string, string>();
    readonly string? _databasePath;
    DateTime _lastRefresh = DateTime.MinValue;
    int _refreshing;

    public string Name => "apps";
    public int Count => _apps.Count;

    /// <summary>The indexed apps, for the "all apps" grid.</summary>
    public IReadOnlyList<AppEntry> All => _apps;

    public AppProvider() : this(AppDatabase.DefaultPath) { }

    public AppProvider(string? databasePath) => _databasePath = databasePath;

    /// <summary>For tests: a fixed app list.</summary>
    internal AppProvider(IReadOnlyList<AppEntry> apps, IReadOnlyDictionary<string, string>? pathExes = null)
    {
        Publish(apps);
        _pathExes = pathExes ?? new Dictionary<string, string>();
        _lastRefresh = DateTime.MaxValue;
    }

    void Publish(IReadOnlyList<AppEntry> apps)
    {
        var owners = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
            foreach (var p in a.Parts)
                if (p.Kind is AppPartKind.Shortcut or AppPartKind.Exe or AppPartKind.Installer or AppPartKind.Tool) owners.TryAdd(p.Target, a);
        _owners = owners;
        _apps = apps;
    }

    /// <summary>The app a file belongs to (its executable, a shortcut to it, its installer), if any.</summary>
    public AppEntry? OwnerOf(string path) => _owners.GetValueOrDefault(path);

    /// <summary>Re-reads the app list if it is older than <paramref name="maxAge"/>. Safe to call often.</summary>
    public async Task RefreshAsync(TimeSpan maxAge)
    {
        if (DateTime.UtcNow - _lastRefresh < maxAge || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            // First run after a start: the saved list answers searches while the scan runs.
            if (_lastRefresh == DateTime.MinValue && _databasePath is not null && _apps.Count == 0)
            {
                try { Publish(await Task.Run(() => AppDatabase.Load(_databasePath)).ConfigureAwait(false)); }
                catch (Exception ex) { Log.Error("app database load failed", ex); }
            }
            // Only an empty list is worth a progress bar; later scans happen quietly.
            using var activity = _apps.Count == 0 ? Activity.ActivityTracker.Shared.Begin("apps", "앱 목록 색인 중") : null;
            var apps = await ShellApps.ScanAsync().ConfigureAwait(false);
            var exes = await Task.Run(ShellApps.ScanPathExecutables).ConfigureAwait(false);
            Publish(apps);
            _pathExes = exes;
            _lastRefresh = DateTime.UtcNow;
            Log.Info($"indexed {apps.Count} apps, {exes.Count} PATH executables");
            if (_databasePath is not null)
            {
                try { await Task.Run(() => AppDatabase.Save(_databasePath, apps)).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("app database save failed", ex); }
            }
        }
        catch (Exception ex) { Log.Error("app index failed", ex); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    /// <summary>True if <paramref name="target"/> is an indexed app's package, shortcut, executable or tool shortcut (not its installer or uninstaller).</summary>
    public bool IsKnownLaunchTarget(string target) =>
        _apps.Any(a => a.Parts.Any(p => p.Kind is AppPartKind.Package or AppPartKind.Shortcut or AppPartKind.Exe or AppPartKind.Tool && string.Equals(p.LaunchTarget, target, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The row for an app: Enter runs <see cref="AppEntry.Launch"/>.</summary>
    public static SearchResult ToResult(AppEntry app, double score = 0)
    {
        var launch = app.Launch;
        return new SearchResult
        {
            Title = app.Name,
            Subtitle = launch.Kind == AppPartKind.Installer ? "애플리케이션 · 설치 파일" : "애플리케이션",
            Kind = ResultKind.App,
            Target = launch.LaunchTarget,
            Arguments = launch.Kind == AppPartKind.Exe ? launch.Arguments : null,
            IconSource = app.IconSource,
            RevealPath = app.FilePath,
            App = app,
            Score = score,
        };
    }

    /// <summary>
    /// A row for one part of an app (the app's preview lists them): Enter runs that part. The uninstaller's command line
    /// is split into program and arguments, and it asks for a second Enter.
    /// </summary>
    public static SearchResult PartResult(AppEntry app, AppPart part)
    {
        var (file, args) = part.Kind == AppPartKind.Uninstaller ? ShellLauncher.SplitCommandLine(part.Target) : (part.LaunchTarget, part.Arguments);
        bool onDisk = part.Kind != AppPartKind.Package && File.Exists(file);
        var notes = new List<string>
        {
            part.Kind switch
            {
                AppPartKind.Package => "스토어 앱",
                AppPartKind.Shortcut => "바로 가기",
                AppPartKind.Exe => "실행 파일",
                AppPartKind.Installer => "설치 파일",
                AppPartKind.Tool => "도구",
                _ => "제거 프로그램",
            },
        };
        if (part == app.Launch) notes.Add("앱의 Enter로 실행");
        if (!part.Exists) notes.Add("없음");
        notes.Add(part.Target + (part.Arguments is null ? "" : " " + part.Arguments));
        return new SearchResult
        {
            // An uninstall command is named after the app it removes: "chrome.exe --uninstall-app-id=..." of a web app is not Chrome.
            Title = part.Kind switch
            {
                AppPartKind.Package => app.Name,
                AppPartKind.Uninstaller when !part.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) => app.Name + " 제거",
                _ => Path.GetFileNameWithoutExtension(file),
            },
            Subtitle = string.Join(" · ", notes),
            Kind = ResultKind.App,
            Target = file,
            Arguments = string.IsNullOrEmpty(args) ? null : args,
            IconSource = part.Kind == AppPartKind.Package ? part.LaunchTarget : onDisk ? file : null,
            RevealPath = onDisk ? file : null,
            RequiresConfirmation = part.Kind == AppPartKind.Uninstaller,
        };
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var q = query.Text;
        var results = new List<SearchResult>();
        foreach (var app in _apps)
        {
            double m = FuzzyMatcher.Score(q, app.Name);
            foreach (var alias in app.Aliases) m = Math.Max(m, FuzzyMatcher.Score(q, alias) - 2); // "file explorer" → 파일 탐색기
            // Desktop apps are also findable by their exe name: "code" -> Visual Studio Code (Code.exe).
            // Not when the shortcut passes arguments: then the program is someone else's (a Chrome web app runs chrome_proxy).
            if (app.Part(AppPartKind.Exe) is { Arguments: null, Target: var fp }) m = Math.Max(m, FuzzyMatcher.Score(q, Path.GetFileNameWithoutExtension(fp)) - 6);
            if (m < query.MinMatch) continue;
            double noise = ShellApps.RoleOf(app.Name) == ShellApps.Role.App ? 0 : 60;
            results.Add(ToResult(app, Scores.AppBase + m - noise));
        }

        // "regedit": the PATH executable itself, unless it is an app already on the list ("cmd" -> Command Prompt).
        if (!query.IsAlternate && _pathExes.TryGetValue(q, out var exe)
            && !(OwnerOf(exe) is { } owner && results.Exists(r => r.App == owner)))
        {
            results.Add(new SearchResult
            {
                Title = Path.GetFileName(exe),
                Subtitle = "애플리케이션",
                Kind = ResultKind.App,
                Target = exe,
                IconSource = exe,
                RevealPath = exe,
                Score = Scores.AppBase + 92,
            });
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}
