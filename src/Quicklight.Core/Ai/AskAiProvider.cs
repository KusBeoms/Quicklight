using Quicklight.Core.Models;
using Quicklight.Core.Providers;

namespace Quicklight.Core.Ai;

/// <summary>A question typed into the launcher becomes a top result that asks the AI assistant set in settings.</summary>
public sealed class AskAiProvider(QuicklightSettings settings) : IResultProvider
{
    public string Name => "ai";

    /// <summary>Above apps, files and web search; below calculator, unit, date, currency and typed paths, which answer on the spot.</summary>
    public const double Score = 890;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate || settings.AiCommand.Trim().Length == 0 || !QuestionDetector.IsQuestion(query.Text))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        return Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                Title = query.Text,
                Subtitle = "AI에게 묻기",
                Kind = ResultKind.Command,
                Target = query.Text,
                Action = ActionType.AskAi,
                Score = Score,
            },
        ]);
    }
}
