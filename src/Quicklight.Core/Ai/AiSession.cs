using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quicklight.Core.Ai;

/// <summary>
/// A chat with the downloaded model. llama-server runs hidden on a local port while the AI is in use and ends after a
/// while idle (it holds the whole model in RAM) or when Quicklight ends. The conversation is kept for follow-ups.
/// </summary>
public sealed partial class AiSession : IDisposable
{
    const string SystemPrompt = "당신은 Windows 런처에 들어 있는 간결한 한국어 어시스턴트입니다. 질문에 짧고 정확하게 답하고, 마크다운 서식은 쓰지 마세요.";
    static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(10);
    const int KeepMessages = 24;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    readonly SemaphoreSlim _gate = new(1, 1); // one question at a time; also guards the server start
    readonly List<(string Role, string Content)> _history = [];
    readonly Timer _idle;
    Process? _server;
    int _port;

    public AiSession() => _idle = new Timer(_ => Stop(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Loads the model ahead of the first question so it answers sooner.</summary>
    public Task WarmUpAsync() => AiModel.IsAvailable ? RunAsync(() => Task.FromResult(""), default) : Task.CompletedTask;

    /// <summary>Answers <paramref name="text"/>; <paramref name="reset"/> starts a new conversation.</summary>
    public Task<string> AskAsync(string text, bool reset, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (reset) _history.Clear();
        _history.Add(("user", text));
        string answer;
        try { answer = await ChatAsync(ct); }
        catch { _history.RemoveAt(_history.Count - 1); throw; }
        _history.Add(("assistant", answer));
        if (_history.Count > KeepMessages) _history.RemoveRange(0, _history.Count - KeepMessages);
        return answer;
    }, ct);

    async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            await EnsureStartedAsync(ct);
            return await work();
        }
        finally
        {
            _idle.Change(IdleStop, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    async Task<string> ChatAsync(CancellationToken ct)
    {
        var messages = _history.Select(m => new { role = m.Role, content = m.Content }).Prepend(new { role = "system", content = SystemPrompt });
        var body = JsonSerializer.Serialize(new
        {
            messages,
            stream = false,
            temperature = 0.7,
            max_tokens = 2048,
            chat_template_kwargs = new { enable_thinking = false }, // the answer, not the model's reasoning, and sooner
        });
        using var resp = await Http.PostAsync($"http://127.0.0.1:{_port}/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"AI 서버 오류 {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return Think().Replace(content, "").Trim();
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex Think();

    async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_server is { HasExited: false }) return;
        if (!AiModel.IsAvailable) throw new InvalidOperationException("AI를 쓰려면 설정에서 모델을 내려받으세요 (RAM 64GB 이상).");
        _port = FreePort();
        var psi = new ProcessStartInfo(AiModel.ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(AiModel.ServerPath)!,
        };
        foreach (var a in new[] { "-m", AiModel.ModelPath, "--host", "127.0.0.1", "--port", _port.ToString(), "-c", "8192" }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("llama-server를 시작하지 못했습니다.");
        KillWithQuicklight(p);
        _server = p;
        // Loading the model takes a while (503 until it is done).
        var until = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (p.HasExited) { _server = null; throw new InvalidOperationException("llama-server가 종료되었습니다."); }
            if (DateTime.UtcNow > until) { Stop(); throw new TimeoutException("모델을 불러오지 못했습니다."); }
            try
            {
                using var r = await Http.GetAsync($"http://127.0.0.1:{_port}/health", ct);
                if (r.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, ct);
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Stop()
    {
        var p = _server;
        _server = null;
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p?.Dispose();
    }

    public void Dispose()
    {
        _idle.Dispose();
        Stop();
    }

    // A job object that kills its processes when Quicklight ends, however it ends: otherwise a crash or an update
    // restart would leave a 6 GB process behind.
    static IntPtr _job;

    static void KillWithQuicklight(Process p)
    {
        if (_job == IntPtr.Zero)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            var info = new JobObjectExtendedLimitInformation { LimitFlags = 0x2000 }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_job, 9, ref info, Marshal.SizeOf<JobObjectExtendedLimitInformation>())) Log.Error("ai job object failed");
        }
        AssignProcessToJobObject(_job, p.Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int size);

    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
