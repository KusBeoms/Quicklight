using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Quicklight.Core.Translation;

public sealed record TranslationResult(string Text, string Source, string Target, double? Confidence, IReadOnlyList<string> Alternatives);

public interface ITranslator
{
    /// <summary>True when the server answers right now.</summary>
    bool IsReady { get; }

    /// <summary>What the engine is doing while not ready ("installing…"), for the launcher's row; null when idle.</summary>
    string? Status { get; }

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
    string? _serverExe;
    readonly TranslationInstaller? _installer;
    volatile string? _status;
    DateTime _installFailedUtc = DateTime.MinValue;
    readonly string[] _models;
    readonly HttpClient _http;
    readonly object _gate = new();
    Task<bool>? _starting;
    Process? _server;
    StreamWriter? _log;
    IntPtr _job;
    volatile bool _ready;
    volatile bool _disposed;
    DateTime _lastProbe = DateTime.MinValue;

    /// <param name="serverExe">libretranslate.exe to start when nothing answers; null to install one with <paramref name="installer"/>.</param>
    public LibreTranslateClient(string baseUrl, string? serverExe, IEnumerable<string>? models = null, HttpMessageHandler? handler = null,
        TranslationInstaller? installer = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        // Only this computer: the text must never leave the machine through a misconfigured URL.
        if (!IsLocal(_baseUri)) throw new ArgumentException("LibreTranslate URL must point to this computer (localhost / 127.0.0.1 / ::1).", nameof(baseUrl));
        _serverExe = serverExe;
        _installer = installer;
        _models = (models ?? DefaultModels).ToArray();
        // No redirects and no proxy: a local process answering with a redirect must not forward the text elsewhere.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.MaxResponseContentBufferSize = 4 << 20;
    }

    public bool IsReady => _ready;

    public string? Status => _status;

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

    static string VenvExe(string dir) => Path.Combine(dir, ".venv", "Scripts", "libretranslate.exe");

    /// <summary>
    /// Finds libretranslate.exe: in the configured folder (from the user's own settings), in a ".library\LibreTranslate"
    /// checkout next to or above the exe, or in the engine Quicklight installed itself. The upward search stops below
    /// the drive root, where any user can create folders, and only accepts a program owned by the current user or an
    /// administrator. Null when there is none yet (the installer then provides one).
    /// </summary>
    public static string? FindServerExe(string? configuredDir, TranslationInstaller? installer = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir)) return File.Exists(VenvExe(configuredDir)) ? VenvExe(configuredDir) : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir?.Parent is not null; dir = dir.Parent)
        {
            var candidate = VenvExe(Path.Combine(dir.FullName, ".library", "LibreTranslate"));
            if (File.Exists(candidate) && IsTrustedOwner(candidate)) return candidate;
        }
        return installer is { IsInstalled: true } ? installer.ServerExe : null;
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException && !ct.IsCancellationRequested)
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
        if (_disposed) return false;
        if (await ProbeAsync(CancellationToken.None).ConfigureAwait(false)) return true;
        if (_disposed) return false;
        if (_server is null || _server.HasExited)
        {
            if (_serverExe is null && _installer is not null)
            {
                // After a failed install, wait before trying again instead of re-downloading on every keystroke.
                if (DateTime.UtcNow - _installFailedUtc < TimeSpan.FromMinutes(10)) return false;
                try
                {
                    using var activity = Activity.ActivityTracker.Shared.Begin("translate", "번역 엔진 설치", 0);
                    // Synchronous IProgress: reports come from pip's output thread, in order.
                    _serverExe = await _installer.InstallAsync(new SyncProgress<InstallProgress>(p =>
                    {
                        _status = p.Text;
                        activity.Report(p.Text, p.Fraction);
                    }), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TranslationException or HttpRequestException or IOException or TaskCanceledException
                                               or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException)
                {
                    Log.Error("translation engine install failed", ex);
                    // Another process installing is not a failure: check again in half a minute, not ten.
                    _installFailedUtc = ex is TranslationInstaller.InstallInProgressException
                        ? DateTime.UtcNow - TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30)
                        : DateTime.UtcNow;
                    _status = "번역 엔진을 설치하지 못했습니다: " + ex.Message;
                    return false;
                }
            }
            if (_serverExe is null) { Log.Error("LibreTranslate is not running and no server program was found"); return false; }
            _status = "번역 엔진을 시작하는 중… 처음에는 언어 모델(약 700MB)을 내려받느라 몇 분 걸립니다";
            try { StartServer(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Error("could not start LibreTranslate", ex);
                return false;
            }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15); // model download on first run
        using var starting = Activity.ActivityTracker.Shared.Begin("translate", "번역 엔진 시작 중");
        long modelsAtStart = ModelBytes();
        long expected = 170L * 1024 * 1024 * _models.Length; // about 170 MB per language model
        while (DateTime.UtcNow < deadline && !_disposed)
        {
            // First start downloads the models: show how much has arrived, else a moving bar.
            long now = ModelBytes();
            if (modelsAtStart < expected / 2 && now > modelsAtStart)
                starting.Report("번역 언어 모델 내려받는 중", Math.Min(0.99, (double)now / expected), $"{now >> 20} / 약 {expected >> 20} MB");
            if (_server is { HasExited: true }) { Log.Error($"LibreTranslate exited with {_server.ExitCode}; see {ServerLogPath}"); return false; }
            if (await ProbeAsync(CancellationToken.None).ConfigureAwait(false)) { Log.Info("LibreTranslate is ready"); _status = null; return true; }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Size of the downloaded argos-translate models (and partial downloads).</summary>
    static long ModelBytes()
    {
        long total = 0;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, ".local", "share", "argos-translate"), Path.Combine(home, ".local", "cache", "argos-translate") })
        {
            try
            {
                if (Directory.Exists(dir))
                    total += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    void StartServer()
    {
        // A previous server that died: release its process object and job before starting another.
        _server?.Dispose();
        _server = null;
        Native.JobObject.Close(ref _job);

        var exe = _serverExe!;
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
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
        _server.OutputDataReceived += (_, e) => WriteLog(log, e.Data);
        _server.ErrorDataReceived += (_, e) => WriteLog(log, e.Data);
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        TieToThisProcess(_server);
        Log.Info($"started LibreTranslate (pid {_server.Id}) on {_baseUri}");
    }

    // Output can still arrive while Quicklight shuts down and closes the log.
    static void WriteLog(StreamWriter log, string? line)
    {
        if (line is null) return;
        try { lock (log) log.WriteLine(line); }
        catch (ObjectDisposedException) { }
    }

    public async Task<TranslationResult> TranslateAsync(string text, string target, CancellationToken ct)
    {
        var body = new JsonObject { ["q"] = text, ["source"] = "auto", ["target"] = target, ["format"] = "text", ["alternatives"] = 3 };
        HttpResponseMessage response;
        try { response = await _http.PostAsync(new Uri(_baseUri, "translate"), JsonContent.Create(body), ct).ConfigureAwait(false); }
        catch (HttpRequestException)
        {
            _ready = false; // the server went away; the next request starts or finds it again
            throw;
        }
        using var _ = response;
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

    // Kill-on-close job: when Quicklight exits (or crashes), Windows ends the server too.
    void TieToThisProcess(Process p)
    {
        _job = Native.JobObject.KillOnClose(p);
        if (_job == IntPtr.Zero) Log.Error("could not tie LibreTranslate to Quicklight");
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        Native.JobObject.Close(ref _job);
        _http.Dispose();
        _log?.Dispose();
    }
}

public class TranslationException(string message) : Exception(message);
