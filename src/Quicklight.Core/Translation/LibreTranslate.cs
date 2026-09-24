using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Quicklight.Core.Translation;

public sealed record TranslationResult(string Text, string Source, string Target, double? Confidence, IReadOnlyList<string> Alternatives);

public interface ITranslator
{
    /// <summary>True when the server answers right now.</summary>
    bool IsReady { get; }

    /// <summary>Starts the server if needed and waits until it answers, up to <paramref name="timeout"/>.</summary>
    Task<bool> EnsureReadyAsync(TimeSpan timeout, CancellationToken ct);

    Task<TranslationResult> TranslateAsync(string text, string target, CancellationToken ct);
}

/// <summary>
/// Client for a local LibreTranslate server (https://github.com/LibreTranslate/LibreTranslate).
/// If nothing answers at the configured URL and a LibreTranslate checkout with a virtual environment is found,
/// the server is started hidden and tied to this process with a kill-on-close job object, so it never outlives Quicklight.
/// </summary>
public sealed class LibreTranslateClient : ITranslator, IDisposable
{
    public static readonly string[] DefaultModels = ["ko", "en", "ja", "zh"];

    readonly Uri _baseUri;
    readonly string? _serverDir;
    readonly string[] _models;
    readonly HttpClient _http;
    readonly object _gate = new();
    Task<bool>? _starting;
    Process? _server;
    StreamWriter? _log;
    IntPtr _job;
    volatile bool _ready;
    DateTime _lastProbe = DateTime.MinValue;

    public LibreTranslateClient(string baseUrl, string? serverDir, IEnumerable<string>? models = null, HttpMessageHandler? handler = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        // Only this computer: the text must never leave the machine through a misconfigured URL.
        if (!IsLocal(_baseUri)) throw new ArgumentException("LibreTranslate URL must point to this computer (localhost / 127.0.0.1 / ::1).", nameof(baseUrl));
        _serverDir = serverDir;
        _models = (models ?? DefaultModels).ToArray();
        // No redirects and no proxy: a local process answering with a redirect must not forward the text elsewhere.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.MaxResponseContentBufferSize = 4 << 20;
    }

    public bool IsReady => _ready;

    /// <summary>
    /// "localhost" or a loopback IP literal. Uri.IsLoopback also accepts names like "loopback", which Windows may resolve
    /// through DNS/LLMNR to another machine, so it is not used.
    /// </summary>
    internal static bool IsLocal(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.IdnHost.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Where the server writes its output (model downloads, errors).</summary>
    public static string ServerLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "libretranslate.log");

    static string ServerExe(string dir) => Path.Combine(dir, ".venv", "Scripts", "libretranslate.exe");

    /// <summary>
    /// Finds a LibreTranslate checkout with a venv: the configured folder (from the user's own settings), else
    /// ".library\LibreTranslate" next to or above the exe. The upward search stops below the drive root, where any user
    /// can create folders, and only accepts a program owned by the current user or an administrator.
    /// </summary>
    public static string? FindServerDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(ServerExe(configured)) ? configured : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir?.Parent is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".library", "LibreTranslate");
            if (File.Exists(ServerExe(candidate)) && IsTrustedOwner(ServerExe(candidate))) return candidate;
        }
        return null;
    }

    static bool IsTrustedOwner(string file)
    {
        try
        {
            var owner = new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null) return false;
            return owner == WindowsIdentity.GetCurrent().User
                || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || owner.IsWellKnown(WellKnownSidType.LocalSystemSid);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { return false; }
    }

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var r = await _http.GetAsync(new Uri(_baseUri, "languages"), cts.Token).ConfigureAwait(false);
            _ready = r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _ready = false;
        }
        _lastProbe = DateTime.UtcNow;
        return _ready;
    }

    public async Task<bool> EnsureReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_ready) return true;
        Task<bool> starting;
        lock (_gate) starting = _starting is { IsCompleted: false } ? _starting : (_starting = Task.Run(StartAndWaitAsync));
        var done = await Task.WhenAny(starting, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return done == starting && await starting.ConfigureAwait(false);
    }

    /// <summary>Probes; if nothing answers, launches the server and polls until it is up (first run downloads models).</summary>
    async Task<bool> StartAndWaitAsync()
    {
        if (await ProbeAsync(CancellationToken.None).ConfigureAwait(false)) return true;
        if (_server is null || _server.HasExited)
        {
            if (_serverDir is null) { Log.Error("LibreTranslate is not running and no server folder with .venv was found"); return false; }
            try { StartServer(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Error("could not start LibreTranslate", ex);
                return false;
            }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15); // model download on first run
        while (DateTime.UtcNow < deadline)
        {
            if (_server is { HasExited: true }) { Log.Error($"LibreTranslate exited with {_server.ExitCode}; see {ServerLogPath}"); return false; }
            if (await ProbeAsync(CancellationToken.None).ConfigureAwait(false)) { Log.Info("LibreTranslate is ready"); return true; }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        return false;
    }

    void StartServer()
    {
        var exe = ServerExe(_serverDir!);
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _serverDir!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--host", _baseUri.Host, "--port", _baseUri.Port.ToString(), "--load-only", string.Join(",", _models),
                     "--disable-web-ui", "--disable-files-translation", "--alternatives-limit", "3", "--char-limit", "5000" })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        Directory.CreateDirectory(Path.GetDirectoryName(ServerLogPath)!);
        // Shared, so a second Quicklight process (the MCP server) can still open the log.
        _log?.Dispose();
        var log = _log = new StreamWriter(new FileStream(ServerLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _server = Process.Start(psi) ?? throw new InvalidOperationException("could not start LibreTranslate");
        _server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        TieToThisProcess(_server);
        Log.Info($"started LibreTranslate (pid {_server.Id}) on {_baseUri}");
    }

    public async Task<TranslationResult> TranslateAsync(string text, string target, CancellationToken ct)
    {
        var body = new JsonObject { ["q"] = text, ["source"] = "auto", ["target"] = target, ["format"] = "text", ["alternatives"] = 3 };
        using var response = await _http.PostAsync(new Uri(_baseUri, "translate"), JsonContent.Create(body), ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TranslationException(Parse(json)?["error"]?.ToString() ?? $"LibreTranslate returned {(int)response.StatusCode}");
        return ParseResult(json, target);
    }

    internal static TranslationResult ParseResult(string json, string target)
    {
        var o = Parse(json) ?? throw new TranslationException("unexpected response");
        var translated = o["translatedText"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new TranslationException("no translatedText");
        string source = "auto";
        double? confidence = null;
        if (o["detectedLanguage"] is JsonObject d)
        {
            if (d["language"] is JsonValue l && l.TryGetValue<string>(out var lang)) source = lang;
            if (d["confidence"] is JsonValue c && c.TryGetValue<double>(out var conf)) confidence = conf;
        }
        var alternatives = (o["alternatives"] as JsonArray)?
            .Select(a => a is JsonValue av && av.TryGetValue<string>(out var t) ? t : null)
            .Where(t => !string.IsNullOrWhiteSpace(t) && t != translated).Select(t => t!).Distinct().Take(3).ToList() ?? [];
        return new TranslationResult(translated, source, target, confidence, alternatives);
    }

    static JsonObject? Parse(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // A job object with KILL_ON_JOB_CLOSE: when Quicklight exits (or crashes), Windows ends the server too.
    void TieToThisProcess(Process p)
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            int size = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch (Exception ex) { Log.Error("could not tie LibreTranslate to Quicklight", ex); }
    }

    public void Dispose()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _http.Dispose();
        _log?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}

public sealed class TranslationException(string message) : Exception(message);
