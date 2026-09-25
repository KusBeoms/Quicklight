using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Quicklight.Core.Ai;

public enum AiLineKind { Answer, Tool, Error }

/// <summary>
/// Turns the assistant's line-based chat output into events. The assistant prints "name> text" for what it says,
/// "  [도구] ..." while it uses a tool, "  [오류] ..." for errors, "[...] ..." log lines, and "나> " when it waits for
/// the next message.
/// </summary>
public sealed partial class AiOutputParser
{
    const string Prompt = "나> ";
    readonly StringBuilder _partial = new();

    public event Action<AiLineKind, string>? Line;
    public event Action? Prompted;

    public void Feed(ReadOnlySpan<char> chunk)
    {
        foreach (char c in chunk)
        {
            if (c == '\n') { EndLine(_partial.ToString().TrimEnd('\r')); _partial.Clear(); }
            else _partial.Append(c);
        }
        // The prompt has no newline after it; it is complete once nothing else follows.
        if (_partial.ToString() == Prompt) { _partial.Clear(); Prompted?.Invoke(); }
    }

    void EndLine(string line)
    {
        while (line.StartsWith(Prompt, StringComparison.Ordinal)) { line = line[Prompt.Length..]; Prompted?.Invoke(); }
        if (line.StartsWith("  [도구] ", StringComparison.Ordinal)) Line?.Invoke(AiLineKind.Tool, line[7..].Trim());
        else if (line.StartsWith("  [오류] ", StringComparison.Ordinal)) Line?.Invoke(AiLineKind.Error, line[7..].Trim());
        else if (Speaker().Match(line) is { Success: true } m) Line?.Invoke(AiLineKind.Answer, m.Groups[1].Value);
    }

    [GeneratedRegex(@"^[^\s\[\]>]+> (.*)$")]
    private static partial Regex Speaker();
}

/// <summary>
/// One long-lived text chat with the assistant program from settings, run hidden with its stdin and stdout piped.
/// Keeping it running keeps the conversation, so follow-up questions work.
/// </summary>
public sealed class AiSession(QuicklightSettings settings)
{
    readonly object _lock = new();
    Process? _process;
    int _promptsToSkip; // prompts that end the startup or a /reset, not an answer
    // Messages still being answered: a new question while an old answer runs must wait for both prompts.
    // ponytail: a reply to the assistant's "진행 또는 취소" gets no prompt of its own, so it may leave this one high.
    int _pending;

    /// <summary>Raised on a background thread.</summary>
    public event Action<AiLineKind, string>? Line;
    /// <summary>The answer is complete. Raised on a background thread.</summary>
    public event Action? AnswerDone;
    /// <summary>The program ended or could not start. Raised on a background thread.</summary>
    public event Action<string>? Failed;

    public bool IsConfigured => settings.AiCommand.Trim().Length > 0;

    /// <summary>Starts the program ahead of the first question so it answers sooner.</summary>
    public void WarmUp()
    {
        if (!IsConfigured) return;
        lock (_lock) EnsureStarted();
    }

    /// <summary>Starts a new conversation with this question.</summary>
    public void Ask(string question) => Send(question, reset: true);

    public void FollowUp(string text) => Send(text, reset: false);

    void Send(string text, bool reset)
    {
        lock (_lock)
        {
            try
            {
                var p = EnsureStarted();
                if (reset) { _promptsToSkip++; p.StandardInput.WriteLine("/reset"); }
                p.StandardInput.WriteLine(text.ReplaceLineEndings(" "));
                _pending++;
                p.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                Log.Error("ai send failed", ex);
                Failed?.Invoke("AI를 실행하지 못했습니다: " + ex.Message);
            }
        }
    }

    Process EnsureStarted()
    {
        if (_process is { HasExited: false } running) return running;
        var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(settings.AiCommand.Trim()), settings.AiArguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.WorkingDirectory = Path.GetDirectoryName(psi.FileName) ?? "";
        // ponytail: no job object; when Quicklight exits the pipe closes and the program ends on end of input.
        var p = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        _process = p;
        _promptsToSkip = 1; // the prompt after startup
        _pending = 0;
        _ = PumpAsync(p);
        return p;
    }

    async Task PumpAsync(Process p)
    {
        var parser = new AiOutputParser();
        parser.Line += (k, t) => Line?.Invoke(k, t);
        parser.Prompted += () =>
        {
            bool done;
            lock (_lock)
            {
                if (_promptsToSkip > 0) { _promptsToSkip--; return; }
                done = _pending > 0 && --_pending == 0;
            }
            if (done) AnswerDone?.Invoke();
        };
        var buf = new char[4096];
        try
        {
            int n;
            while ((n = await p.StandardOutput.ReadAsync(buf, 0, buf.Length)) > 0) parser.Feed(buf.AsSpan(0, n));
        }
        catch (Exception ex) { Log.Error("ai read failed", ex); }
        lock (_lock) if (ReferenceEquals(_process, p)) _process = null;
        Failed?.Invoke("AI가 종료되었습니다. 다시 질문하면 새로 시작합니다.");
    }
}
