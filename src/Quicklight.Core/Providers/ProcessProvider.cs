using System.Diagnostics;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>"kill chrome" / "프로세스 크롬": running processes by name; Enter twice ends every process of that name.</summary>
public sealed class ProcessProvider : IResultProvider
{
    static readonly string[] Prefixes = ["kill ", "프로세스 ", "종료 "];

    public string Name => "process";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var prefix = Prefixes.FirstOrDefault(p => query.Text.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        var q = query.Text[prefix.Length..].Trim();
        var alt = Hangul.HangulToQwerty(q); // "kill 촏ㄱㄷ" typed with the IME on

        var results = new List<SearchResult>();
        int self = Environment.ProcessId;
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        foreach (var group in Process.GetProcesses().GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var procs = group.Where(p => p.Id != self && p.SessionId == session).ToList();
                if (procs.Count == 0) continue;
                double m = q.Length == 0 ? 50 : FuzzyMatcher.Score(q, group.Key);
                if (alt is not null) m = Math.Max(m, FuzzyMatcher.Score(alt, group.Key) - 4);
                if (m < 50) continue;
                long memory = procs.Sum(p => { try { return p.WorkingSet64; } catch { return 0; } });
                string? exe = null;
                try { exe = procs[0].MainModule?.FileName; } catch { } // access denied for elevated processes
                results.Add(new SearchResult
                {
                    Title = group.Key,
                    Subtitle = $"프로세스 {procs.Count}개 · {memory / 1024 / 1024:#,0} MB · Enter 두 번으로 종료",
                    Kind = ResultKind.Process,
                    Target = group.Key,
                    Action = ActionType.Kill,
                    RequiresConfirmation = true,
                    IconSource = exe,
                    Score = Scores.Process + m,
                });
            }
            finally { foreach (var p in group) p.Dispose(); }
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    /// <summary>Ends every process named <paramref name="name"/> in this session, with its child processes.</summary>
    public static void Kill(string name)
    {
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
            {
                if (p.Id == Environment.ProcessId || p.SessionId != session) continue;
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Log.Error("kill " + name, ex); }
            }
    }
}
