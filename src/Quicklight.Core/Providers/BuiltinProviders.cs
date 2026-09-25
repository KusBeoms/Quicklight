using System.Text.RegularExpressions;
using Quicklight.Core.Calc;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

public sealed class CalculatorProvider : IResultProvider
{
    public string Name => "calculator";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate || !Calculator.TryEvaluate(query.Text, out var v)) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        return Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                Title = Calculator.Format(v),
                Subtitle = $"{query.Text.Trim().TrimEnd('=').Trim()} =",
                Kind = ResultKind.Calculator,
                Target = Calculator.FormatPlain(v),
                Action = ActionType.Copy,
                Score = Scores.Calculator,
            },
        ]);
    }
}

public sealed partial class UrlProvider : IResultProvider
{
    public string Name => "url";

    static readonly HashSet<string> Tlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "io", "dev", "app", "ai", "co", "kr", "jp", "us", "uk", "de", "fr", "cn", "me", "tv", "info", "biz",
        "edu", "gov", "xyz", "gg", "so", "sh", "to", "ly", "fm", "cc", "ca", "au", "eu", "ru", "in", "site", "online", "tech", "page", "cloud",
    };

    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*://\S+$", RegexOptions.IgnoreCase)] private static partial Regex SchemeUrl();
    [GeneratedRegex(@"^(?<host>(?:[\w\-]+\.)+(?<tld>[a-z]{2,}))(?::\d{1,5})?(?:[/?#]\S*)?$", RegexOptions.IgnoreCase)] private static partial Regex Domain();
    [GeneratedRegex(@"^(?:localhost|\d{1,3}(?:\.\d{1,3}){3})(?::\d{1,5})?(?:/\S*)?$", RegexOptions.IgnoreCase)] private static partial Regex Local();

    /// <summary>Returns the URL to open, or null if the text is not URL-like.</summary>
    public static (string Url, bool Explicit)? Parse(string text)
    {
        var t = text.Trim();
        if (t.Contains(' ')) return null;
        if (SchemeUrl().IsMatch(t) && Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp")
            return (t, true);
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && Domain().IsMatch(t)) return ("https://" + t, true);
        if (Local().IsMatch(t)) return ("http://" + t, true);
        var m = Domain().Match(t);
        if (m.Success && Tlds.Contains(m.Groups["tld"].Value)) return ("https://" + t, false);
        return null;
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate || Parse(query.Text) is not { } p) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        return Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                Title = p.Url,
                Subtitle = "브라우저에서 열기",
                Kind = ResultKind.Url,
                Target = p.Url,
                Score = p.Explicit ? Scores.ExplicitUrl : Scores.BareDomain,
            },
        ]);
    }
}

/// <summary>Typed paths: "C:\Project", "%APPDATA%", "~\Downloads", "\\server\share", with child completion for "C:\Pro".</summary>
/// <param name="probeRemote">
/// False skips UNC paths entirely. Checking whether "\\host\share" exists makes Windows sign in to that host,
/// which must not happen for text an AI client was tricked into sending.
/// </param>
public sealed class PathProvider(bool probeRemote = true) : IResultProvider
{
    public static bool IsRemoteOrDevice(string path) => path.StartsWith(@"\\") || path.StartsWith("//");

    public string Name => "path";

    public static string? Expand(string text)
    {
        var t = text.Trim().Trim('"');
        if (t.Length == 0) return null;
        if (t == "~" || t.StartsWith("~\\") || t.StartsWith("~/"))
            t = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + t[1..];
        if (t.Contains('%')) t = Environment.ExpandEnvironmentVariables(t);
        t = t.Replace('/', '\\');
        if (t.Length >= 2 && t[1] == ':') t = char.ToUpperInvariant(t[0]) + t[1..];
        if (t.Length == 2 && t[1] == ':') t += "\\";
        bool rooted = (t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && t[2] == '\\') || t.StartsWith(@"\\");
        return rooted ? t : null;
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        var path = query.IsAlternate ? null : Expand(query.Text);
        if (path is null || (!probeRemote && IsRemoteOrDevice(path))) return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                results.Add(Make(path, Scores.ExactPath));
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                var prefix = Path.GetFileName(path);
                if (dir is not null && Directory.Exists(dir))
                {
                    int i = 0;
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos(prefix + "*"))
                    {
                        if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        results.Add(Make(entry.FullName, Scores.PathCompletion - i));
                        if (++i >= 8) break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    static SearchResult Make(string path, double score)
    {
        bool isDir = Directory.Exists(path);
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return new SearchResult
        {
            Title = string.IsNullOrEmpty(name) ? path : name,
            Subtitle = path,
            Kind = ResultKind.Path,
            Target = path,
            IconSource = path,
            RevealPath = path,
            Score = score + (isDir ? 1 : 0),
        };
    }
}

public sealed class WebSearchProvider(QuicklightSettings settings) : IResultProvider
{
    public string Name => "web";

    public static SearchResult Make(string text, QuicklightSettings settings)
    {
        var url = settings.WebSearchUrl.Replace("{0}", Uri.EscapeDataString(text.Trim()));
        string engine = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "웹";
        return new SearchResult
        {
            Title = $"“{text.Trim()}” 웹에서 검색",
            Subtitle = engine,
            Kind = ResultKind.WebSearch,
            Target = url,
            Score = Scores.WebSearch,
        };
    }

    /// <summary>"yt 고양이": the engine keyed "yt" in settings, searching for the rest. Null if the first word is no engine key.</summary>
    public static SearchResult? MakeKeyword(string text, QuicklightSettings settings)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[1].Length == 0 || !settings.SearchEngines.TryGetValue(parts[0].ToLowerInvariant(), out var template)) return null;
        var url = template.Replace("{0}", Uri.EscapeDataString(parts[1]));
        string engine = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : parts[0];
        return new SearchResult
        {
            Title = $"“{parts[1]}” {engine}에서 검색",
            Subtitle = $"{parts[0]} 키워드 검색",
            Kind = ResultKind.WebSearch,
            Target = url,
            Score = Scores.KeywordSearch,
        };
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        var keyword = MakeKeyword(query.Text, settings);
        return Task.FromResult<IReadOnlyList<SearchResult>>(keyword is null ? [Make(query.Text, settings)] : [keyword, Make(query.Text, settings)]);
    }
}
