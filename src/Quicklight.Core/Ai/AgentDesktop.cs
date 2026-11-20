using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quicklight.Core.Ai;

/// <param name="Blocked">Windows refused to start the program (Smart App Control, Defender): computer control is off for this run.</param>
public sealed record AgentDesktopResult(bool Ok, string? ErrorCode, string? ErrorMessage, JsonNode? Data, string Raw, bool Blocked);

/// <summary>
/// agent-desktop (vercel-labs, Apache-2.0): a CLI that reads and drives other programs through Windows UI Automation.
/// Downloaded on demand into %LOCALAPPDATA%\Quicklight\ai\agent-desktop (one pinned release, checked against the
/// release's checksums.txt before it is unpacked). Every command is a one-shot process that prints one JSON envelope.
/// </summary>
public static class AgentDesktop
{
    public const string Version = "v1.3.1";
    const string Repository = "vercel-labs/agent-desktop";
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static string Directory { get; } = Path.Combine(AiModel.Directory, "agent-desktop");
    public static string ExePath => ExePathOverride ?? Path.Combine(Directory, "agent-desktop.exe");
    /// <summary>Its state (sessions, snapshots, screenshots); relocated from ~/.agent-desktop so Quicklight leaves nothing there.</summary>
    public static string Home => HomeOverride ?? Path.Combine(Directory, "home");

    public static bool IsInstalled => File.Exists(ExePath);

    // Tests point these at a fake program.
    internal static string? ExePathOverride { get; set; }
    internal static string? HomeOverride { get; set; }

    static string? _blocked; // the first time Windows refuses to start it, later calls fail at once

    internal static void ResetForTests() { _blocked = null; ExePathOverride = null; HomeOverride = null; }

    /// <summary>Why computer control cannot be used (Korean), or null when it can.</summary>
    public static string? CheckAvailable() => _blocked ?? (IsInstalled ? null : "컴퓨터 제어 기능이 설치되어 있지 않습니다. 설정에서 내려받으세요.");

    /// <summary>Removes the downloaded program and its state.</summary>
    public static void Delete()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        _blocked = null;
    }

    // ---- install ----

    // The archives' SHA-256 for this version, in the code: a release whose files were replaced (checksums.txt with them)
    // is not installed. Update with Version.
    static readonly Dictionary<string, string> PinnedSha256 = new()
    {
        ["x86_64"] = "6ee4b1e078f4e78792e79f16e32e4a78b9919464aa3c55629b7e27ed53a08c13",
        ["aarch64"] = "805f1f61890f9983fceaf38cda94a37a049c379435d65607db5cedd22c8ef97b",
    };

    const int MaxOutputChars = 4_000_000; // a huge snapshot must not take hundreds of MB; the excess is read and dropped

    public static async Task InstallAsync(IProgress<(string Label, double? Fraction)> progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        var asset = $"agent-desktop-{Version}-{arch}-pc-windows-msvc.tar.gz";
        var http = AiModel.Client();

        progress.Report(("컴퓨터 제어 확인 정보 받는 중", null));
        var checksums = await http.GetStringAsync($"https://github.com/{Repository}/releases/download/{Version}/checksums.txt", ct);
        var expected = checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == asset)?[0]
            ?? throw new InvalidOperationException("컴퓨터 제어 프로그램의 확인 정보를 찾지 못했습니다.");

        var work = Path.Combine(Directory, "download");
        if (System.IO.Directory.Exists(work)) System.IO.Directory.Delete(work, true);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, asset);
            await AiModel.DownloadAsync($"https://github.com/{Repository}/releases/download/{Version}/{asset}", archive, "컴퓨터 제어 내려받는 중", progress, ct);

            // Before anything is unpacked: the archive must be exactly the release's.
            string actual;
            await using (var f = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase) || !actual.Equals(PinnedSha256[arch], StringComparison.OrdinalIgnoreCase))
                throw new IOException("내려받은 컴퓨터 제어 프로그램이 확인 정보와 달라 설치하지 않았습니다.");

            // The archive holds only the exe; Apache-2.0 asks for the license text to travel with it.
            progress.Report(("라이선스 내려받는 중", null));
            await File.WriteAllTextAsync(Path.Combine(Directory, "LICENSE"), await http.GetStringAsync($"https://raw.githubusercontent.com/{Repository}/{Version}/LICENSE", ct), ct);

            progress.Report(("컴퓨터 제어 설치 중", null));
            var staging = Path.Combine(work, "files");
            System.IO.Directory.CreateDirectory(staging);
            await ExtractAsync(archive, staging, ct);
            var exe = Path.Combine(staging, "agent-desktop.exe");
            if (!File.Exists(exe)) throw new IOException("내려받은 파일에서 컴퓨터 제어 프로그램을 찾지 못했습니다.");
            File.Move(exe, Path.Combine(Directory, "agent-desktop.exe"), overwrite: true);
        }
        finally { try { System.IO.Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

        // Starting it once shows right away whether Windows lets it run.
        _blocked = null;
        var check = await RunAsync(["version"], ct);
        if (check.Blocked) throw new InvalidOperationException(_blocked!);
    }

    static async Task ExtractAsync(string archive, string target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-xf", archive, "-C", target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("압축을 풀지 못했습니다.");
        var err = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new IOException("압축을 풀지 못했습니다: " + await err);
    }

    // ---- run ----

    /// <summary>Runs one command and parses its JSON envelope. Cancelling or timing out kills the process tree.</summary>
    public static async Task<AgentDesktopResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (CheckAvailable() is { } why) return Failure(_blocked is not null ? "BLOCKED" : "NOT_INSTALLED", why, blocked: _blocked is not null);

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        psi.Environment["AGENT_DESKTOP_HOME"] = Home;
        foreach (var a in args) psi.ArgumentList.Add(a);
        System.IO.Directory.CreateDirectory(Home);

        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("start failed"); }
        catch (Win32Exception ex) { return OnStartFailure(ex); }

        using (p)
        using (var timer = new CancellationTokenSource(timeout ?? DefaultTimeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token))
        {
            var stdout = ReadLimitedAsync(p.StandardOutput, linked.Token);
            var stderr = p.StandardError.ReadToEndAsync(linked.Token); // drained so a full pipe never stalls the program
            try
            {
                await p.WaitForExitAsync(linked.Token);
                return Parse(await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                ct.ThrowIfCancellationRequested();
                return Failure("TIMEOUT", "컴퓨터 제어 명령이 제한 시간 안에 끝나지 않았습니다.");
            }
        }
    }

    /// <summary>Reads to the end but keeps at most <see cref="MaxOutputChars"/>; the rest is drained so the program never blocks on a full pipe.</summary>
    static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
            if (sb.Length < MaxOutputChars) sb.Append(buf, 0, Math.Min(n, MaxOutputChars - sb.Length));
        return sb.ToString();
    }

    // Win32 errors for "Windows will not run this program": access denied, virus/malware found (Defender quarantine),
    // blocked by policy (Smart App Control and App Control, 1260 / 1261 / 4551).
    static readonly HashSet<int> BlockCodes = [5, 225, 226, 1260, 1261, 4551];

    internal static bool IsBlockCode(Win32Exception ex) => BlockCodes.Contains(ex.NativeErrorCode);

    internal static AgentDesktopResult OnStartFailure(Win32Exception ex)
    {
        Log.Error("agent-desktop did not start", ex);
        if (!IsBlockCode(ex)) return Failure("START_FAILED", $"컴퓨터 제어 프로그램을 시작하지 못했습니다 ({ex.NativeErrorCode}).");
        _blocked = "Windows가 컴퓨터 제어 프로그램(agent-desktop)의 실행을 막았습니다. 스마트 앱 컨트롤이나 Windows 보안에서 허용해야 쓸 수 있습니다. 컴퓨터 제어만 꺼지고 다른 Qutra 기능은 그대로 쓸 수 있습니다.";
        return Failure("BLOCKED", _blocked, blocked: true);
    }

    static AgentDesktopResult Failure(string code, string message, bool blocked = false) => new(false, code, message, null, "", blocked);

    static AgentDesktopResult Parse(string stdout, string stderr)
    {
        JsonNode? root = null;
        try { root = JsonNode.Parse(stdout); }
        catch (JsonException) { }
        if (root is not JsonObject o || o["ok"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            var text = (stdout + stderr).Trim();
            return Failure("BAD_OUTPUT", "컴퓨터 제어 프로그램의 응답을 읽지 못했습니다: " + (text.Length > 200 ? text[..200] : text));
        }
        bool ok = o["ok"]!.GetValue<bool>();
        return new AgentDesktopResult(ok, o["error"]?["code"]?.GetValue<string>(), o["error"]?["message"]?.GetValue<string>(), o["data"], stdout.Trim(), false);
    }
}
