using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>
/// A query that starts with a program on PATH or a cmd built-in ("git status", "ipconfig /all") becomes a row that
/// runs it in the launcher's mini terminal (handled by the launcher). Off unless <see cref="QuicklightSettings.Terminal"/>.
/// </summary>
public sealed class TerminalProvider(QuicklightSettings settings) : IResultProvider
{
    public string Name => "terminal";

    static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
        { "dir", "echo", "type", "ver", "set", "cd", "cls", "tree", "vol", "date", "time", "mkdir", "md", "rmdir", "rd", "copy", "move", "del", "ren", "mklink" };

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var text = query.Text;
        // "cmd ipconfig": the word cmd only says "run this", the terminal is cmd already.
        bool prefixed = text.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase) && text[4..].TrimStart() is { Length: > 0 };
        if (prefixed) text = text[4..].TrimStart();
        if (!settings.Terminal || query.IsAlternate || !IsCommand(text)) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        bool hasArgs = prefixed || text.Contains(' ');
        return Task.FromResult<IReadOnlyList<SearchResult>>([new SearchResult
        {
            Title = text,
            Subtitle = "터미널에서 실행",
            Kind = ResultKind.Terminal,
            Target = text,
            Action = ActionType.Terminal,
            Score = hasArgs ? Scores.Terminal : Scores.AppBase + 20, // a lone word is more likely an app's name
        }]);
    }

    /// <summary>The first word names a cmd built-in or an executable on PATH. ponytail: PATH is probed per keystroke, cache it if a slow PATH entry shows up.</summary>
    public static bool IsCommand(string text)
    {
        var word = text.Split(' ', 2)[0];
        if (word.Length == 0 || word.IndexOfAny(['\\', '/', ':', '"']) >= 0) return false;
        if (Builtins.Contains(word)) return true;
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try { if (File.Exists(Path.Combine(dir, word + ext))) return true; }
                catch (ArgumentException) { break; } // a malformed PATH entry
            }
        return false;
    }
}
