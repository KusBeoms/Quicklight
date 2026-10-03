using System.Collections.Concurrent;
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
        foreach (var group in Snapshot())
        {
            double m = q.Length == 0 ? 50 : FuzzyMatcher.Score(q, group.Name);
            if (alt is not null) m = Math.Max(m, FuzzyMatcher.Score(alt, group.Name) - 4);
            if (m < 50) continue;
            results.Add(new SearchResult
            {
                Title = group.Name,
                Subtitle = $"프로세스 {group.Count}개 · {group.Memory / 1024 / 1024:#,0} MB · Enter 두 번으로 종료",
                Kind = ResultKind.Process,
                Target = group.Name,
                Action = ActionType.Kill,
                RequiresConfirmation = true,
                IconSource = ExeOf(group),
                Score = Scores.Process + m,
            });
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    sealed record Group(string Name, int Count, long Memory, int FirstId);

    static readonly object Gate = new();
    static List<Group> _snapshot = [];
    static long _snapshotAt; // 0: never taken (TickCount64 - long.MinValue would overflow and never refresh)
    // A program's location rarely changes; reading it (MainModule) is the slow part, so once per name.
    static readonly ConcurrentDictionary<string, string> Exes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One listing serves every stage of a keystroke and a burst of typing ("kill " alone matches every process).</summary>
    static List<Group> Snapshot()
    {
        lock (Gate)
        {
            if (_snapshotAt == 0 || Environment.TickCount64 - _snapshotAt > 1000) { _snapshot = List(); _snapshotAt = Environment.TickCount64; }
            return _snapshot;
        }
    }

    static List<Group> List()
    {
        int self = Environment.ProcessId;
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var all = Process.GetProcesses();
        try
        {
            var groups = new List<Group>();
            foreach (var group in all.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                var procs = group.Where(p => p.Id != self && p.SessionId == session).ToList();
                if (procs.Count == 0) continue;
                long memory = procs.Sum(p => { try { return p.WorkingSet64; } catch { return 0; } });
                groups.Add(new Group(group.Key, procs.Count, memory, procs[0].Id));
            }
            return groups;
        }
        finally { foreach (var p in all) p.Dispose(); }
    }

    static string? ExeOf(Group group)
    {
        if (Exes.TryGetValue(group.Name, out var known)) return known.Length == 0 ? null : known;
        try
        {
            using var p = Process.GetProcessById(group.FirstId);
            var exe = p.MainModule?.FileName;
            if (exe is not null) Exes[group.Name] = exe;
            return exe;
        }
        catch (System.ComponentModel.Win32Exception) { Exes[group.Name] = ""; return null; } // elevated: denied every time
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; } // exited: ask again
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
        lock (Gate) _snapshotAt = 0; // opened again right away, the list must not show them
    }
}
