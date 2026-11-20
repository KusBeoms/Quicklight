using Quicklight.Core.Models;
using Quicklight.Core.Providers;

namespace Quicklight.Core.Ai;

/// <summary>A question typed into the launcher becomes a top result that asks the local AI (once it is downloaded).</summary>
public sealed class AskAiProvider(Func<bool>? available = null) : IResultProvider
{
    readonly Func<bool> _available = available ?? (() => AiModel.IsAvailable);

    public string Name => "ai";

    /// <summary>Above apps, files and web search; below calculator, unit, date, currency and typed paths, which answer on the spot.</summary>
    public const double Score = 890;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (query.IsAlternate || !QuestionDetector.IsQuestion(query.Text) || !_available())
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        return Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                Title = query.Text,
                Subtitle = "Qutra에게 묻기",
                Kind = ResultKind.Command,
                Target = query.Text,
                Action = ActionType.AskAi,
                Score = Score,
            },
        ]);
    }
}
