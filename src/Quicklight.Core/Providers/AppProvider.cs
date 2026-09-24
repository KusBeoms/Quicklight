using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Shell;

namespace Quicklight.Core.Providers;

/// <summary>Start menu apps (shell:AppsFolder) plus exact-name executables on PATH ("regedit", "cmd").</summary>
public sealed class AppProvider : IResultProvider
{
    /// <summary>Start menu entries that are not really apps: uninstallers, help links, readmes.</summary>
    internal static readonly string[] NoiseWords =
        ["uninstall", "제거", "readme", "read me", "documentation", "도움말", "help", "website", "웹 사이트", "release notes", "license", "manual"];

    volatile IReadOnlyList<AppEntry> _apps = [];
    volatile IReadOnlyDictionary<string, string> _pathExes = new Dictionary<string, string>();
    DateTime _lastRefresh = DateTime.MinValue;
    int _refreshing;

    public string Name => "apps";
    public int Count => _apps.Count;

    /// <summary>The indexed apps, for the "all apps" grid.</summary>
    public IReadOnlyList<AppEntry> All => _apps;

    public AppProvider() { }

    /// <summary>For tests: a fixed app list.</summary>
    internal AppProvider(IReadOnlyList<AppEntry> apps, IReadOnlyDictionary<string, string>? pathExes = null)
    {
        _apps = apps;
        _pathExes = pathExes ?? new Dictionary<string, string>();
        _lastRefresh = DateTime.MaxValue;
    }

    /// <summary>Re-reads the app list if it is older than <paramref name="maxAge"/>. Safe to call often.</summary>
    public async Task RefreshAsync(TimeSpan maxAge)
    {
        if (DateTime.UtcNow - _lastRefresh < maxAge || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var apps = await ShellApps.EnumerateAsync().ConfigureAwait(false);
            var exes = await Task.Run(ShellApps.ScanPathExecutables).ConfigureAwait(false);
            _apps = apps;
            _pathExes = exes;
            _lastRefresh = DateTime.UtcNow;
            Log.Info($"indexed {apps.Count} apps, {exes.Count} PATH executables");
        }
        catch (Exception ex) { Log.Error("app index failed", ex); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    /// <summary>True if <paramref name="target"/> is the shell:AppsFolder launch target of an indexed app.</summary>
    public bool IsKnownLaunchTarget(string target) =>
        _apps.Any(a => string.Equals(a.LaunchTarget, target, StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var q = query.Text;
        var results = new List<SearchResult>();
        foreach (var app in _apps)
        {
            double m = FuzzyMatcher.Score(q, app.Name);
            // Desktop apps are also findable by their exe name: "code" -> Visual Studio Code (Code.exe).
            if (app.FilePath is { } fp && fp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                m = Math.Max(m, FuzzyMatcher.Score(q, Path.GetFileNameWithoutExtension(fp)) - 6);
            if (m < query.MinMatch) continue;

            var lower = app.Name.ToLowerInvariant();
            double noise = NoiseWords.Any(w => lower.Contains(w)) ? 60 : 0;
            results.Add(new SearchResult
            {
                Title = app.Name,
                Subtitle = app.FilePath ?? "앱",
                Kind = ResultKind.App,
                Target = app.LaunchTarget,
                IconSource = app.LaunchTarget,
                RevealPath = app.FilePath,
                Score = Scores.AppBase + m - noise,
            });
        }

        if (!query.IsAlternate && _pathExes.TryGetValue(q, out var exe))
        {
            results.Add(new SearchResult
            {
                Title = Path.GetFileName(exe),
                Subtitle = exe,
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
