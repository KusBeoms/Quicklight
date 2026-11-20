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
    public const long MinRamBytes = 64L << 30;

    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "ai");
    public static string ModelPath => Path.Combine(Directory, ModelFile);
    public static string ServerPath => Path.Combine(Directory, "llama", "llama-server.exe");

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

    static HttpClient Client()
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
    /// a partial download). <paramref name="progress"/> gets a label and, when known, the fraction done.
    /// </summary>
    public static async Task InstallAsync(IProgress<(string Label, double? Fraction)> progress, CancellationToken ct)
    {
        if (!HasEnoughRam) throw new InvalidOperationException("RAM 64GB 이상에서만 사용할 수 있습니다.");
        System.IO.Directory.CreateDirectory(Directory);
        if (!File.Exists(ServerPath)) await InstallRuntimeAsync(progress, ct);
        if (!File.Exists(ModelPath))
            await DownloadAsync($"https://huggingface.co/{Repository}/resolve/main/{ModelFile}", ModelPath, "모델 내려받는 중", progress, ct);
    }

    static async Task InstallRuntimeAsync(IProgress<(string, double?)> progress, CancellationToken ct)
    {
        progress.Report(("실행 파일 찾는 중", null));
        // /releases/latest can point at a release whose assets are still being built, so take the newest that has the zip.
        using var list = JsonDocument.Parse(await Client().GetStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5", ct));
        // The Vulkan build runs on any GPU (NVIDIA, AMD, Intel) and offloads the model by itself, and falls back to the
        // CPU when there is no device. Without a Vulkan driver (vulkan-1.dll) the CPU build is enough.
        // ponytail: CUDA builds are ~10x larger downloads for a little more speed on NVIDIA; add if Vulkan proves too slow.
        var build = VulkanAvailable ? "-bin-win-vulkan-x64.zip" : "-bin-win-cpu-x64.zip";
        var url = list.RootElement.EnumerateArray()
            .SelectMany(r => r.GetProperty("assets").EnumerateArray())
            .Select(a => a.GetProperty("browser_download_url").GetString()!)
            .FirstOrDefault(u => u.EndsWith(build, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("llama.cpp 릴리스를 찾지 못했습니다.");
        var zip = Path.Combine(Directory, "llama.zip");
        await DownloadAsync(url, zip, "실행 파일 내려받는 중", progress, ct);
        var target = Path.Combine(Directory, "llama");
        var staging = target + ".new";
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
        System.IO.Directory.Move(staging, target);
        File.Delete(zip);
    }

    /// <summary>Downloads to <paramref name="path"/>.part, resuming it, and renames it when the size matches.</summary>
    static async Task DownloadAsync(string url, string path, string label, IProgress<(string, double?)> progress, CancellationToken ct)
    {
        var part = path + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); throw new IOException("내려받다 만 파일이 맞지 않아 지웠습니다. 다시 시도해 주세요."); }
        resp.EnsureSuccessStatusCode();
        bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) have = 0; // the server ignored Range: start over
        long total = have + (resp.Content.Headers.ContentLength ?? -1);

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
