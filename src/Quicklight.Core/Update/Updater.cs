using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Quicklight.Core.Update;

public sealed record ReleaseInfo(Version Version, string Tag, string AssetUrl, long AssetSize, string Notes, DateTime Published);

/// <summary>
/// Self-update from GitHub Releases, like an app updater: finds the latest release of the configured repository,
/// downloads its Quicklight.exe asset next to the running exe, swaps the files (a running exe can be renamed on Windows),
/// starts the new version and leaves the old file for the new process to delete.
/// </summary>
public sealed class Updater(string repository, HttpMessageHandler? handler = null, string? publicKeyPem = null) : IDisposable
{
    /// <summary>
    /// The release asset: Quicklight.exe and its signature Quicklight.exe.sig, zipped together as
    /// "Quicklight_v0.2.0.zip" (publish.bat) or plain "Quicklight.zip".
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex AssetName =
        new(@"^Quicklight(_v\d+(\.\d+){1,3})?\.zip$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool IsReleaseAsset(string? name) => name is not null && AssetName.IsMatch(name);
    public const string ExeName = "Quicklight.exe";
    readonly string _publicKeyPem = publicKeyPem ?? UpdateSignature.PublicKeyPem;
    public const string ProductName = "Quicklight";

    readonly HttpClient _http = CreateClient(handler);

    static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var c = handler is null ? new HttpClient() : new HttpClient(handler);
        c.Timeout = TimeSpan.FromMinutes(10);
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Quicklight/{CurrentVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>This build's version (from the assembly; set with publish.bat &lt;version&gt;).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Latest release with a Quicklight_v*.zip (or Quicklight.zip) asset, or null if there is none.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        if (!IsValidRepository(repository)) throw new UpdateException($"'{repository}' is not a GitHub repository (owner/name).");
        using var response = await _http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode}.");
        return ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    internal static bool IsValidRepository(string repo) =>
        System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");

    internal static ReleaseInfo? ParseRelease(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o) return null;
        var tag = Str(o["tag_name"]) ?? "";
        if (o["draft"]?.GetValue<bool>() == true || o["prerelease"]?.GetValue<bool>() == true) return null;
        if (ParseVersion(tag) is not { } version) return null;
        foreach (var a in o["assets"] as JsonArray ?? [])
        {
            if (a is not JsonObject asset || !IsReleaseAsset(Str(asset["name"]))) continue;
            var url = Str(asset["browser_download_url"]);
            // Downloads come from github.com only (it redirects to its own storage host).
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "github.com") continue;
            long size = asset["size"] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : 0;
            DateTime.TryParse(Str(o["published_at"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var published);
            return new ReleaseInfo(version, tag, url, size, Str(o["body"]) ?? "", published);
        }
        return null;
    }

    /// <summary>"v1.2.3", "1.2", "Quicklight 1.2.3" → Version.</summary>
    internal static Version? ParseVersion(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Downloads Quicklight.zip, checks that its Quicklight.exe carries a valid signature from the release key and
    /// is a newer version than <paramref name="current"/>, and writes it next to <paramref name="exePath"/> as
    /// "Quicklight.exe.new". Anything that fails a check is deleted and reported.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string exePath, Version current, IProgress<double>? progress, CancellationToken ct)
    {
        var zipPath = exePath + ".zip.part";
        var target = exePath + ".new";
        try
        {
            using (var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.AssetSize;
                if (total > MaxBytes) throw new UpdateException("The download is larger than 1 GB.");
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (done > MaxBytes) throw new UpdateException("The download is larger than 1 GB.");
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (release.AssetSize > 0 && new FileInfo(zipPath).Length != release.AssetSize) throw new UpdateException("The download is incomplete.");

            var (exe, signature) = ReadPackage(zipPath);
            if (!UpdateSignature.Verify(exe, signature, _publicKeyPem))
                throw new UpdateException("The release signature does not match: this update was not signed by the Quicklight release key.");
            await File.WriteAllBytesAsync(target, exe, ct).ConfigureAwait(false);
            var version = Verify(target, exe.Length);
            // The version inside the signed exe decides, not the release tag: an old signed build re-published
            // under a new tag must not downgrade anyone.
            if (version <= current) throw new UpdateException($"The release contains v{version.ToString(3)}, which is not newer than v{current.ToString(3)}.");
        }
        catch
        {
            TryDelete(target); // failed, cancelled or rejected: nothing half-checked stays behind
            throw;
        }
        finally { TryDelete(zipPath); }
        return target;
    }

    const long MaxBytes = 1L << 30;

    /// <summary>Reads Quicklight.exe and Quicklight.exe.sig from the release zip (other entries are ignored).</summary>
    internal static (byte[] Exe, string Signature) ReadPackage(string zipPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var exeEntry = zip.GetEntry(ExeName) ?? throw new UpdateException($"The release zip has no {ExeName}.");
            var sigEntry = zip.GetEntry(ExeName + ".sig") ?? throw new UpdateException($"The release zip has no {ExeName}.sig signature.");
            if (exeEntry.Length > MaxBytes || sigEntry.Length > 4096) throw new UpdateException("The release zip has unexpected sizes.");
            // Read exactly the declared sizes and make sure nothing more comes out: the header is not trusted.
            var exe = ReadExactly(exeEntry);
            var sig = ReadExactly(sigEntry);
            return (exe, System.Text.Encoding.ASCII.GetString(sig));
        }
        catch (InvalidDataException) { throw new UpdateException("The download is not a valid zip file."); }
    }

    static byte[] ReadExactly(System.IO.Compression.ZipArchiveEntry entry)
    {
        var buffer = new byte[entry.Length];
        using var s = entry.Open();
        try { s.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw new UpdateException("The release zip is damaged."); }
        if (s.ReadByte() != -1) throw new UpdateException("The release zip is damaged (an entry is larger than it claims).");
        return buffer;
    }

    /// <summary>The file must be complete, a Windows program and this product; returns its version.</summary>
    internal static Version Verify(string file, long expectedSize)
    {
        var info = new FileInfo(file);
        if (expectedSize > 0 && info.Length != expectedSize) throw new UpdateException("The download is incomplete.");
        using (var fs = File.OpenRead(file))
        {
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new UpdateException("The download is not a Windows program.");
        }
        var product = FileVersionInfo.GetVersionInfo(file).ProductName;
        if (!string.Equals(product, ProductName, StringComparison.Ordinal))
            throw new UpdateException($"The download is not Quicklight (product '{product}').");
        var v = FileVersionInfo.GetVersionInfo(file);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
    }

    /// <summary>
    /// Swaps in the downloaded exe and starts it. The caller exits right after, releasing its single-instance lock;
    /// the new process waits for that and deletes the old file.
    /// </summary>
    public static void ApplyAndRestart(string downloaded, string exePath)
    {
        var old = Swap(downloaded, exePath);
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! };
        psi.ArgumentList.Add("--updated");
        psi.ArgumentList.Add(old);
        try { Process.Start(psi)?.Dispose(); }
        catch
        {
            // The new version would not start (e.g. blocked by antivirus): put the running version back in place.
            TryDelete(exePath);
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>Puts <paramref name="downloaded"/> in place of <paramref name="exePath"/>; returns where the previous exe went.</summary>
    internal static string Swap(string downloaded, string exePath)
    {
        // A unique name: an older .old may still be mapped by a "Quicklight.exe --mcp" process and cannot be replaced.
        var old = $"{exePath}.old-{Guid.NewGuid():N}";
        File.Move(exePath, old);                 // allowed while running: the image stays mapped under its new name
        try { File.Move(downloaded, exePath); }
        catch
        {
            File.Move(old, exePath);             // put the working version back
            throw;
        }
        return old;
    }

    /// <summary>Run by the new version: deletes the previous exe once its process has exited (and any stale leftovers).</summary>
    public static async Task CleanupAsync(string? oldFile, string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var name = Path.GetFileName(exePath);
        var leftovers = new List<string?> { oldFile, exePath + ".new" };
        try { leftovers.AddRange(Directory.EnumerateFiles(dir, name + ".old*")); } catch (IOException) { }
        foreach (var leftover in leftovers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (leftover is null || !File.Exists(leftover)) continue;
            // Only files next to us, never an arbitrary path from the command line.
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(leftover)), Path.GetDirectoryName(exePath), StringComparison.OrdinalIgnoreCase)) continue;
            // Files still in use (an MCP process of the old version) are left for the next start.
            for (int i = 0; i < 60 && File.Exists(leftover); i++)
            {
                if (TryDelete(leftover)) break;
                await Task.Delay(500).ConfigureAwait(false); // the old process is still shutting down
            }
        }
    }

    static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

public sealed class UpdateException(string message) : Exception(message);
