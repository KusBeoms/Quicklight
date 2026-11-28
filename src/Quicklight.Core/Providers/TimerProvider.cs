using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Quicklight.Core.Models;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Quicklight.Core.Providers;

public sealed record PendingTimer(string Id, DateTime Due, string Message);

/// <summary>
/// Timers and reminders: "5분", "1시간 30분", "25:00", "2h", "10분 뒤 회의", "3시 회의", "내일 9시 병원"; Enter schedules a Windows toast.
/// Bare "5m" / "5s" are not timers (meters, seconds in the unit converter). "타이머" / "알림" lists the pending ones (Del cancels).
/// Target of a start row: "in|seconds|message" or "at|local ticks|message"; of a pending row: "cancel|id".
/// </summary>
public sealed partial class TimerProvider(Func<DateTime>? clock = null, Func<IReadOnlyList<PendingTimer>>? pending = null) : IResultProvider
{
    public string Name => "timer";

    static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    const string Token = @"\d+(?:\.\d+)?\s*(?:시간|hours?|hrs?|hr|h|분|minutes?|mins?|min|초|seconds?|secs?|sec)(?:반)?";

    [GeneratedRegex($@"^(?:(?:타이머|timer)\s*)?((?:{Token}\s*)+|\d+:\d{{2}}(?::\d{{2}})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Timer();

    [GeneratedRegex($@"^((?:{Token}\s*)+)(?:뒤|후)\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();

    [GeneratedRegex(@"^(내일\s*)?(오전|오후|am|pm)?\s*(\d{1,2})(?:시(?!간)\s*(?:(\d{1,2})분|(반))?|:(\d{2})|(?=\s*(?:am|pm)\b))\s*(am|pm)?\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Absolute();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(시간|hours?|hrs?|hr|h|분|minutes?|mins?|min|초|seconds?|secs?|sec)(반)?", RegexOptions.IgnoreCase)]
    private static partial Regex OneToken();

    [GeneratedRegex(@"^(?:타이머|timer|알림)$", RegexOptions.IgnoreCase)]
    private static partial Regex ListQuery();

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct) =>
        Task.FromResult(query.IsAlternate ? [] : Evaluate(query.Text, (clock ?? (() => DateTime.Now))(), (pending ?? TimerService.Pending)()));

    public static IReadOnlyList<SearchResult> Evaluate(string text, DateTime now, IReadOnlyList<PendingTimer> pending)
    {
        text = text.Trim();
        if (ListQuery().IsMatch(text))
        {
            var rows = pending.OrderBy(p => p.Due).Select((p, i) => Row($"{p.Message} — {Remaining(p.Due - now)} 남음",
                $"{p.Due.ToString("tt h:mm", Korean)} · Del 로 취소", $"cancel|{p.Id}", Scores.Timer - i)).ToList();
            rows.Add(new SearchResult { Title = "시계 앱 열기", Subtitle = "Windows 시계", Kind = ResultKind.Command, Target = "ms-clock:", Score = Scores.Timer - 50 });
            return rows;
        }

        if (Timer().Match(text) is { Success: true } t && Seconds(t.Groups[1].Value) is { } secs and > 0)
            return [Row($"타이머 {Remaining(TimeSpan.FromSeconds(secs))} 시작", $"{now.AddSeconds(secs).ToString("tt h:mm:ss", Korean)}에 알림", $"in|{secs}|타이머")];

        if (Relative().Match(text) is { Success: true } r && Seconds(r.Groups[1].Value) is { } rs and > 0)
            return [Row($"알림: {r.Groups[2].Value} — {now.AddSeconds(rs).ToString("tt h:mm", Korean)}", $"{Remaining(TimeSpan.FromSeconds(rs))} 뒤", $"in|{rs}|{r.Groups[2].Value}")];

        if (Absolute().Match(text) is { Success: true } a)
        {
            int h = int.Parse(a.Groups[3].Value);
            int m = a.Groups[4].Success ? int.Parse(a.Groups[4].Value) : a.Groups[5].Success ? 30 : a.Groups[6].Success ? int.Parse(a.Groups[6].Value) : 0;
            var mer = (a.Groups[2].Success ? a.Groups[2] : a.Groups[7]).Value.ToLowerInvariant();
            if (h > 24 || m > 59) return [];
            var due = Next(now, a.Groups[1].Success, mer, h, m);
            return [Row($"알림: {a.Groups[8].Value} — {due.ToString("tt h:mm", Korean)}", $"{(due.Date == now.Date ? "오늘" : due.Date == now.Date.AddDays(1) ? "내일" : due.ToString("M월 d일"))} · {Remaining(due - now)} 뒤", $"at|{due.Ticks}|{a.Groups[8].Value}")];
        }
        return [];
    }

    /// <summary>Next occurrence of h:m. Without 오전/오후 (and not "내일") a 1-12 hour means whichever of the two comes first.</summary>
    static DateTime Next(DateTime now, bool tomorrow, string meridiem, int h, int m)
    {
        if (meridiem is "오후" or "pm") h = h % 12 + 12;
        else if (meridiem is "오전" or "am") h %= 12;
        var day = tomorrow ? now.Date.AddDays(1) : now.Date;
        var candidates = new List<DateTime> { day.AddHours(h).AddMinutes(m) };
        if (meridiem == "" && !tomorrow && h is >= 1 and <= 12) candidates.Add(day.AddHours(h % 12 + 12).AddMinutes(m));
        candidates.AddRange(candidates.ToArray().Select(c => c.AddDays(1)));
        return candidates.Where(c => c > now).Min();
    }

    static long? Seconds(string s)
    {
        s = s.Trim();
        if (s.Contains(':'))
        {
            var p = s.Split(':').Select(int.Parse).ToArray(); // "25:00" is minutes:seconds, "1:30:00" hours:minutes:seconds
            return p.Length == 3 ? p[0] * 3600 + p[1] * 60 + p[2] : p[0] * 60 + p[1];
        }
        double total = 0;
        while (s.Length > 0 && OneToken().Match(s) is { Success: true } m)
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var u = m.Groups[2].Value.ToLowerInvariant();
            total += n * (u[0] is 'h' or '시' ? 3600 : u[0] is 'm' or '분' ? 60 : 1) + (m.Groups[3].Success ? (u[0] is 'h' or '시' ? 1800 : 30) : 0);
            s = s[m.Length..].TrimStart();
        }
        return s.Length == 0 ? (long)Math.Round(total) : null;
    }

    static string Remaining(TimeSpan t)
    {
        long s = (long)Math.Ceiling(t.TotalSeconds);
        var parts = new List<string>();
        if (s >= 3600) parts.Add($"{s / 3600}시간");
        if (s / 60 % 60 > 0) parts.Add($"{s / 60 % 60}분");
        if (s % 60 > 0 || parts.Count == 0) parts.Add($"{s % 60}초");
        return string.Join(" ", parts);
    }

    static SearchResult Row(string title, string subtitle, string target, double score = Scores.Timer) => new()
    {
        Title = title, Subtitle = subtitle, Kind = ResultKind.Command, Target = target, Action = ActionType.Timer, Score = score,
    };
}

/// <summary>
/// Schedules Windows toasts (delivered by Windows even after Quicklight exits). Unpackaged apps need an AppUserModelID: it is
/// registered under HKCU\Software\Classes\AppUserModelId (no Start-menu shortcut needed). The "5분 다시 알림" button uses a
/// quicklight-snooze: protocol link, which starts "Quicklight.exe --snooze" (no COM activator). Without a usable toast
/// (exception, under ~6 s) an in-process timer calls <see cref="Fallback"/> (the tray balloon).
/// </summary>
public static class TimerService
{
    const string Aumid = "Quicklight.Launcher";
    const string Group = "ql";
    const string Protocol = "quicklight-snooze:";

    public static Action<string, string>? Fallback;

    static readonly List<(PendingTimer Info, System.Threading.Timer Timer)> Local = [];

    [DllImport("shell32.dll")]
    static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Launcher start-up.</summary>
    public static void Init(Action<string, string> fallback)
    {
        Fallback = fallback;
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); Register(); }
        catch (Exception ex) { Log.Error("toast registration failed", ex); }
    }

    static void Register()
    {
        var exe = Environment.ProcessPath!;
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
        {
            k.SetValue("DisplayName", "Quicklight");
            k.SetValue("IconUri", exe);
        }
        using var p = Registry.CurrentUser.CreateSubKey(@"Software\Classes\quicklight-snooze");
        p.SetValue("", "URL:Quicklight snooze");
        p.SetValue("URL Protocol", "");
        using var c = p.CreateSubKey(@"shell\open\command");
        c.SetValue("", $"\"{exe}\" --snooze \"%1\"");
    }

    /// <summary>"in|seconds|message" or "at|ticks|message".</summary>
    public static void Start(string target)
    {
        var p = target.Split('|', 3);
        Schedule(p[0] == "in" ? DateTime.Now.AddSeconds(long.Parse(p[1])) : new DateTime(long.Parse(p[1])), p[2]);
    }

    public static void Schedule(DateTime due, string message)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        if (due - DateTime.Now > TimeSpan.FromSeconds(6))
        {
            try
            {
                var xml = new XmlDocument();
                var text = System.Security.SecurityElement.Escape(message);
                xml.LoadXml($"<toast scenario=\"reminder\"><visual><binding template=\"ToastGeneric\"><text>{text}</text><text>Quicklight</text></binding></visual>" +
                    $"<actions><action content=\"5분 다시 알림\" arguments=\"{Protocol}{Uri.EscapeDataString(message)}\" activationType=\"protocol\"/>" +
                    "<action content=\"닫기\" arguments=\"dismiss\" activationType=\"system\"/></actions></toast>");
                ToastNotificationManager.CreateToastNotifier(Aumid).AddToSchedule(new ScheduledToastNotification(xml, new DateTimeOffset(due)) { Id = id, Tag = id, Group = Group });
                return;
            }
            catch (Exception ex) { Log.Error("toast schedule failed, using tray balloon", ex); }
        }
        var info = new PendingTimer(id, due, message);
        var wait = due - DateTime.Now;
        var timer = new System.Threading.Timer(_ =>
        {
            lock (Local) Local.RemoveAll(l => l.Info.Id == id);
            Fallback?.Invoke("Quicklight", message);
        }, null, wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        lock (Local) Local.Add((info, timer));
    }

    public static IReadOnlyList<PendingTimer> Pending()
    {
        var list = new List<PendingTimer>();
        try
        {
            foreach (var s in ToastNotificationManager.CreateToastNotifier(Aumid).GetScheduledToastNotifications().Where(s => s.Group == Group))
                list.Add(new PendingTimer(s.Tag, s.DeliveryTime.LocalDateTime, s.Content.GetElementsByTagName("text")[0].InnerText));
        }
        catch (Exception ex) { Log.Error("scheduled toast list failed", ex); }
        lock (Local) list.AddRange(Local.Select(l => l.Info));
        return list;
    }

    public static void Cancel(string id)
    {
        try
        {
            var n = ToastNotificationManager.CreateToastNotifier(Aumid);
            foreach (var s in n.GetScheduledToastNotifications().Where(s => s.Group == Group && s.Tag == id)) n.RemoveFromSchedule(s);
        }
        catch (Exception ex) { Log.Error("toast cancel failed", ex); }
        lock (Local)
        {
            foreach (var l in Local.Where(l => l.Info.Id == id)) l.Timer.Dispose();
            Local.RemoveAll(l => l.Info.Id == id);
        }
    }

    /// <summary>"Quicklight.exe --snooze quicklight-snooze:message": schedule the same message 5 minutes from now, then exit.</summary>
    public static int Snooze(string url)
    {
        try
        {
            Register();
            var message = Uri.UnescapeDataString(url.StartsWith(Protocol, StringComparison.OrdinalIgnoreCase) ? url[Protocol.Length..].TrimEnd('/') : url);
            Schedule(DateTime.Now.AddMinutes(5), message);
            return 0;
        }
        catch (Exception ex) { Log.Error("snooze failed", ex); return 1; }
    }
}
