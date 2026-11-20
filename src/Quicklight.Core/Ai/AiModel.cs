using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Quicklight.Core.Ai;

/// <summary>
/// The local AI: a GGUF model from Hugging Face run by llama.cpp's llama-server, both downloaded from settings into
/// %LOCALAPPDATA%\Quicklight\ai. Needs 64 GB of RAM; below that only the MCP server is offered.
/// </summary>
public static class AiModel
{
    public const string Repository = "HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive";
    // ponytail: one fixed quant (Q4_K_M, 5.6 GB, the fastest on CPU); Q6_K and Q8_0 are in the same repo if quality matters more.
    public const string ModelFile = "Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf";
    /// <summary>The vision projector (~0.92 GB) that lets the model see images; without it images are only mentioned by path.</summary>
    public const string VisionFile = "mmproj-Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-BF16.gguf";
    public const long MinRamBytes = 64L << 30;
    // Pinned, so a resumed download can never join two versions of a file and a new llama.cpp cannot change flags under
    // an install that worked (this pair was tried together). Move them on purpose.
    const string ModelRevision = "0a41c68809d375475f954be12ba7c40efa56c2a9";
    const string LlamaRelease = "b12568";

    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "ai");
    public static string ModelPath => Path.Combine(Directory, ModelFile);
    public static string ServerPath => Path.Combine(Directory, "llama", "llama-server.exe");
    public static string VisionPath => Path.Combine(Directory, VisionFile);

    public static bool HasVision => File.Exists(VisionPath);

    public static bool IsInstalled => File.Exists(ModelPath) && File.Exists(ServerPath);
    /// <summary>The AI is offered: enough RAM and everything downloaded.</summary>
    public static bool IsAvailable => HasEnoughRam && IsInstalled;

    static readonly Lazy<long> Ram = new(InstalledRamBytes);
    public static long RamBytes => Ram.Value;
    public static bool HasEnoughRam => RamBytes >= MinRamBytes;

    [DllImport("kernel32.dll")]
    static extern bool GetPhysicallyInstalledSystemMemory(out long kilobytes);

    /// <summary>Installed RAM (a 64 GB machine reports 64 GB here, but a little less as "total physical" after hardware reservations).</summary>
    static long InstalledRamBytes()
    {
        try { if (GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0) return kb * 1024; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        // ponytail: the fallback is the memory the OS lets us use, which is below the installed amount; it can only under-report.
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    static bool VulkanAvailable => File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static HttpClient Client()
    {
        if (!Http.DefaultRequestHeaders.UserAgent.Any()) Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Quicklight", "1"));
        return Http;
    }

    /// <summary>Removes the downloaded runtime and model.</summary>
    public static void Delete()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>
    /// Downloads whatever is missing: llama.cpp (a small zip from its latest GitHub release), then the model (resumes
    /// a partial download), then the vision projector unless <paramref name="includeVision"/> is false.
    /// <paramref name="progress"/> gets a label and, when known, the fraction done.
    /// </summary>
    public static async Task InstallAsync(IProgress<(string Label, double? Fraction)> progress, CancellationToken ct, bool includeVision = true)
    {
        if (!HasEnoughRam) throw new InvalidOperationException("RAM 64GB 이상에서만 사용할 수 있습니다.");
        System.IO.Directory.CreateDirectory(Directory);
        if (!File.Exists(ServerPath)) await InstallRuntimeAsync(progress, ct);
        if (!File.Exists(ModelPath))
            await DownloadAsync($"https://huggingface.co/{Repository}/resolve/{ModelRevision}/{ModelFile}", ModelPath, "모델 내려받는 중", progress, ct);
        if (includeVision && !HasVision)
            await DownloadAsync($"https://huggingface.co/{Repository}/resolve/{ModelRevision}/{VisionFile}", VisionPath, "이미지 인식 파일 내려받는 중", progress, ct);
    }

    static async Task InstallRuntimeAsync(IProgress<(string, double?)> progress, CancellationToken ct)
    {
        progress.Report(("실행 파일 찾는 중", null));
        using var release = JsonDocument.Parse(await Client().GetStringAsync($"https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/{LlamaRelease}", ct));
        // The Vulkan build runs on any GPU (NVIDIA, AMD, Intel) and offloads the model by itself, and falls back to the
        // CPU when there is no device. Without a Vulkan driver (vulkan-1.dll) the CPU build is enough.
        // ponytail: CUDA builds are ~10x larger downloads for a little more speed on NVIDIA; add if Vulkan proves too slow.
        var build = VulkanAvailable ? "-bin-win-vulkan-x64.zip" : "-bin-win-cpu-x64.zip";
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("browser_download_url").GetString()!.EndsWith(build, StringComparison.Ordinal));
        if (asset.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("llama.cpp 릴리스를 찾지 못했습니다.");
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var zip = Path.Combine(Directory, "llama.zip");
        await DownloadAsync(url, zip, "실행 파일 내려받는 중", progress, ct);
        // GitHub lists each asset's SHA-256 ("digest": "sha256:..."): a corrupt or swapped download is not unpacked.
        if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string actual;
            await using (var f = File.OpenRead(zip)) actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new IOException("내려받은 실행 파일의 해시가 맞지 않아 지웠습니다. 다시 시도해 주세요.");
            }
        }
        var target = Path.Combine(Directory, "llama");
        var staging = target + ".new";
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
        System.IO.Directory.Move(staging, target);
        File.Delete(zip);
    }

    /// <summary>Downloads to <paramref name="path"/>.part, resuming it, and renames it when the size matches.</summary>
    internal static async Task DownloadAsync(string url, string path, string label, IProgress<(string, double?)> progress, CancellationToken ct)
    {
        var part = path + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (have > 0 && resp.Content.Headers.ContentRange?.Length == have) { File.Move(part, path, overwrite: true); return; } // it was already whole
            File.Delete(part);
            throw new IOException("내려받다 만 파일이 맞지 않아 지웠습니다. 다시 시도해 주세요.");
        }
        resp.EnsureSuccessStatusCode();
        bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) have = 0; // the server ignored Range: start over
        long total = resp.Content.Headers.ContentLength is { } length ? have + length : -1; // unknown length: no size check

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[1 << 17];
            long done = have;
            int n;
            var last = Environment.TickCount64;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (Environment.TickCount64 - last < 200) continue;
                last = Environment.TickCount64;
                progress.Report(($"{label} {done >> 20:N0} / {(total > 0 ? total >> 20 : 0):N0} MB", total > 0 ? (double)done / total : null));
            }
        }
        if (total > 0 && new FileInfo(part).Length != total) throw new IOException("내려받은 파일 크기가 맞지 않습니다. 다시 시도하면 이어서 받습니다.");
        File.Move(part, path, overwrite: true);
    }
}
