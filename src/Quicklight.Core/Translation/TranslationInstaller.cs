using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Quicklight.Core.Translation;

/// <summary>
/// Installs the translation engine on first use, so Quicklight needs nothing installed beforehand.
/// LibreTranslate with its models is about 1 GB, too large to carry inside the exe; instead this downloads
/// Python (python.org's official NuGet build, checked against a pinned SHA-256) and installs LibreTranslate with pip
/// into %LOCALAPPDATA%\Quicklight\translate. The server downloads its language models on its first start.
/// </summary>
/// <param name="Fraction">0..1 of the whole install, or null while it cannot be measured.</param>
public sealed record InstallProgress(string Text, double? Fraction);

public sealed class TranslationInstaller(string? root = null, HttpMessageHandler? handler = null)
{
    public const string LibreTranslateVersion = "1.9.6";
    const string RequirementsResource = "Quicklight.libretranslate-requirements.txt";
    const string PythonUrl = "https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg";
    const string PythonSha256 = "0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8";

    public string Root { get; } = root ?? DefaultRoot;

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "translate");

    public string PythonDir => Path.Combine(Root, "python");
    public string ServerExe => Path.Combine(PythonDir, "Scripts", "libretranslate.exe");
    string Marker => Path.Combine(Root, $"installed-{LibreTranslateVersion}");
    public string LogPath => Path.Combine(Root, "install.log");

    public bool IsInstalled => File.Exists(Marker) && File.Exists(ServerExe);

    /// <summary>Returns the server executable, installing first if needed. Reports human-readable steps.</summary>
    public async Task<string> InstallAsync(IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (IsInstalled) return ServerExe;
        Directory.CreateDirectory(Root);

        // One installer at a time, across processes (the launcher and an MCP server may both want translation).
        FileStream installLock;
        try { installLock = new FileStream(Path.Combine(Root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InstallInProgressException(); }
        using var _ = installLock;
        if (IsInstalled) return ServerExe;

        if (!File.Exists(Path.Combine(PythonDir, "python.exe")))
        {
            progress?.Report(new("번역 엔진 설치 1/2: Python 내려받는 중 (15MB)", 0));
            var package = Path.Combine(Root, "python.nupkg");
            // Python is the first tenth of the install; the packages are the rest.
            await DownloadAsync(PythonUrl, package, f => progress?.Report(new("번역 엔진 설치 1/2: Python 내려받는 중 (15MB)", 0.1 * f)), ct).ConfigureAwait(false);
            try
            {
                if (!Sha256(package).Equals(PythonSha256, StringComparison.OrdinalIgnoreCase))
                    throw new TranslationException("The downloaded Python package does not match its expected checksum.");
                // Extract next to it, then rename: a half-extracted Python is never mistaken for a finished one.
                var staging = PythonDir + ".tmp";
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                ExtractTools(package, staging);
                if (Directory.Exists(PythonDir)) Directory.Delete(PythonDir, recursive: true);
                Directory.Move(staging, PythonDir);
            }
            finally { File.Delete(package); }
        }

        const string packagesText = "번역 엔진 설치 2/2: LibreTranslate 설치 중 (약 350MB, 몇 분 걸립니다)";
        progress?.Report(new(packagesText, 0.1));
        var python = Path.Combine(PythonDir, "python.exe");
        // Every package pinned by version and SHA-256 (generated from a tested install), so nothing on PyPI can change what runs.
        var requirements = Path.Combine(Root, "requirements.txt");
        await using (var src = typeof(TranslationInstaller).Assembly.GetManifestResourceStream(RequirementsResource)
                               ?? throw new TranslationException("missing requirements lock"))
        await using (var dst = File.Create(requirements))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        // pip prints "Collecting <package>" once per package of the lock; after the last one it only installs.
        int total = File.ReadLines(requirements).Count(l => l.Length > 0 && !l.StartsWith('#'));
        int collected = 0;
        void OnLine(string line)
        {
            if (!line.StartsWith("Collecting ", StringComparison.Ordinal)) return;
            int n = Interlocked.Increment(ref collected);
            progress?.Report(new(packagesText, 0.1 + 0.85 * Math.Min(1.0, (double)n / Math.Max(1, total))));
        }
        await RunAsync(python, ["-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location",
            "--require-hashes", "--no-deps", "-r", requirements], ct, OnLine).ConfigureAwait(false);
        progress?.Report(new("번역 엔진 설치 마무리 중", 0.97));
        if (!File.Exists(ServerExe)) throw new TranslationException($"LibreTranslate did not install; see {LogPath}");
        File.WriteAllText(Marker, DateTime.UtcNow.ToString("O"));
        return ServerExe;
    }

    /// <summary>Another Quicklight process holds the install lock: not a failure, just not our turn.</summary>
    public sealed class InstallInProgressException() : TranslationException("다른 Quicklight가 번역 엔진을 설치하고 있습니다");

    async Task DownloadAsync(string url, string path, Action<double> fraction, CancellationToken ct)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(path);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) fraction((double)done / total);
        }
    }

    internal static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Extracts the package's tools/ folder (the Python installation), refusing entries that escape the target.</summary>
    internal static void ExtractTools(string package, string targetDir)
    {
        var full = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(package);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("tools/", StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName["tools/".Length..]));
            if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new TranslationException("Unsafe path in the Python package.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    async Task RunAsync(string exe, string[] args, CancellationToken ct, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var log = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        using var p = Process.Start(psi) ?? throw new TranslationException("could not start Python");
        // pip ends with Quicklight: an interrupted install must not keep running (or race the next one).
        var job = Native.JobObject.KillOnClose(p);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.WriteLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
        finally { Native.JobObject.Close(ref job); }
        if (p.ExitCode != 0) throw new TranslationException($"pip failed ({p.ExitCode}); see {LogPath}");
    }
}
