using Quicklight.Core.Models;
using Quicklight.Core.Update;

namespace Quicklight.Core.Providers;

/// <summary>
/// "-update" / "--update" / "-업데이트": a row that checks GitHub Releases right away and installs a newer Quicklight.
/// "-version" / "--version" / "-버전": a row with the running version. The leading dash keeps them apart from file and app searches.
/// </summary>
public sealed class UpdateProvider(QuicklightSettings settings) : IResultProvider
{
    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "update", "updates", "upgrade", "업데이트", "업그레이드", "quicklight update", "update quicklight", "퀵라이트 업데이트" };

    public string Name => "update";

    static readonly HashSet<string> VersionKeywords = new(StringComparer.OrdinalIgnoreCase) { "version", "ver", "버전" };

    /// <summary>The command word of "-word" / "--word", or null when the text has no one or two leading dashes.</summary>
    static string? CommandWord(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("--")) return t[2..].TrimStart();
        return t.StartsWith('-') ? t[1..].TrimStart() : null;
    }

    public static bool IsUpdateQuery(string text) => CommandWord(text) is { } w && Keywords.Contains(w);

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        if (CommandWord(query.Text) is { } word && VersionKeywords.Contains(word))
        {
            var version = Updater.CurrentVersion.ToString(3);
            return Task.FromResult<IReadOnlyList<SearchResult>>(
            [
                new SearchResult
                {
                    Title = $"Quicklight v{version}",
                    Subtitle = $"실행 중인 버전   ·   {Environment.ProcessPath}   ·   Enter로 버전 복사",
                    Kind = ResultKind.Command,
                    Target = version,
                    Action = ActionType.Copy,
                    Score = Scores.Calculator + 10,
                },
            ]);
        }
        if (!IsUpdateQuery(query.Text)) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        return Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                Title = "Quicklight 업데이트",
                Subtitle = $"현재 v{Updater.CurrentVersion.ToString(3)}   ·   GitHub({settings.UpdateRepository})에서 최신 버전을 확인하고, 있으면 바로 설치하고 다시 시작",
                Kind = ResultKind.Command,
                Target = "update",
                Action = ActionType.Update,
                Score = Scores.Calculator + 10, // an exact command word: always on top
            },
        ]);
    }
}
