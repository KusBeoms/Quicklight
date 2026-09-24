using Quicklight.Core.Models;
using Quicklight.Core.Update;

namespace Quicklight.Core.Providers;

/// <summary>"update" / "업데이트": a row that checks GitHub Releases right away and installs a newer Quicklight.</summary>
public sealed class UpdateProvider(QuicklightSettings settings) : IResultProvider
{
    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "update", "updates", "upgrade", "업데이트", "업그레이드", "quicklight update", "update quicklight", "퀵라이트 업데이트" };

    public string Name => "update";

    public static bool IsUpdateQuery(string text) => Keywords.Contains(text.Trim());

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate || !IsUpdateQuery(query.Text)) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
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
