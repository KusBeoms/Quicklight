using Quicklight.Core.Matching;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>Settings "snippets": typing a key (or words of the text) offers the text; Enter pastes it.</summary>
public sealed class SnippetProvider(QuicklightSettings settings) : IResultProvider
{
    public string Name => "snippets";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var (key, text) in settings.Snippets)
        {
            double m = string.Equals(query.Text, key, StringComparison.OrdinalIgnoreCase) ? 100 : FuzzyMatcher.Score(query.Text, key);
            if (!query.IsAlternate && text.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) m = Math.Max(m, 60);
            if (m < query.MinMatch) continue;
            results.Add(new SearchResult
            {
                Title = OneLine(text),
                Subtitle = $"스니펫 · {key}",
                Kind = ResultKind.Snippet,
                Target = text,
                Action = ActionType.Paste,
                Score = (m >= 100 ? Scores.Snippet : Scores.AppBase) + m,
            });
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public static string OneLine(string s) => s.ReplaceLineEndings(" ⏎ ").Trim();
}

/// <summary>Settings "commands": a name (and aliases) that runs a program with fixed arguments.</summary>
public sealed class CustomCommandProvider(QuicklightSettings settings) : IResultProvider
{
    public string Name => "commands";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var c in settings.Commands)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Path)) continue;
            double m = FuzzyMatcher.Score(query.Text, c.Name);
            foreach (var a in c.Aliases ?? []) m = Math.Max(m, FuzzyMatcher.Score(query.Text, a) - 2);
            if (m < query.MinMatch) continue;
            var path = Environment.ExpandEnvironmentVariables(c.Path);
            results.Add(new SearchResult
            {
                Title = c.Name,
                Subtitle = "사용자 명령",
                Kind = ResultKind.Command,
                Target = path,
                Arguments = string.IsNullOrWhiteSpace(c.Arguments) ? null : Environment.ExpandEnvironmentVariables(c.Arguments),
                IconSource = File.Exists(path) ? path : null,
                Score = Scores.AppBase + m + 1, // a command the user wrote beats an app of the same name
            });
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}
