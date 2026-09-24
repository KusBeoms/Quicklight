using System.IO.Compression;
using System.Net;
using Quicklight.Core;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Core.Translation;
using Quicklight.Core.Update;

namespace Quicklight.Tests;

public class UpdaterTests
{
    const string Release = """
        {"tag_name":"v0.2.0","draft":false,"prerelease":false,"published_at":"2026-09-25T01:02:03Z","body":"notes",
         "assets":[{"name":"README.md","browser_download_url":"https://github.com/KusBeoms/Quicklight/releases/download/v0.2.0/README.md","size":10},
                   {"name":"Quicklight.zip","browser_download_url":"https://github.com/KusBeoms/Quicklight/releases/download/v0.2.0/Quicklight.zip","size":1234}]}
        """;

    [Fact]
    public void Parses_the_latest_release()
    {
        var r = Updater.ParseRelease(Release)!;
        Assert.Equal(new Version(0, 2, 0), r.Version);
        Assert.EndsWith("/Quicklight.zip", r.AssetUrl);
        Assert.Equal(1234, r.AssetSize);
    }

    [Theory]
    [InlineData("""{"tag_name":"v1.0.0","assets":[]}""")]                                    // no zip attached
    [InlineData("""{"tag_name":"v1.0.0","assets":[{"name":"Quicklight.exe","browser_download_url":"https://github.com/a/b/x","size":1}]}""")] // bare exe: unsigned, not accepted
    [InlineData("""{"tag_name":"latest","assets":[{"name":"Quicklight.zip","browser_download_url":"https://github.com/a/b/x","size":1}]}""")] // no version
    [InlineData("""{"tag_name":"v1.0.0","prerelease":true,"assets":[{"name":"Quicklight.zip","browser_download_url":"https://github.com/a/b/x","size":1}]}""")]
    [InlineData("""{"tag_name":"v1.0.0","assets":[{"name":"Quicklight.zip","browser_download_url":"https://evil.example/x.exe","size":1}]}""")] // not github.com
    [InlineData("""{"tag_name":"v1.0.0","assets":[{"name":"Quicklight.zip","browser_download_url":"http://github.com/a/b/x","size":1}]}""")]    // not https
    public void Ignores_unusable_releases(string json) => Assert.Null(Updater.ParseRelease(json));

    [Theory]
    [InlineData("Quicklight_v0.2.0.zip", true)]
    [InlineData("quicklight_v1.10.3.zip", true)]
    [InlineData("Quicklight.zip", true)]
    [InlineData("Quicklight.exe", false)]         // an unsigned bare exe is never an update
    [InlineData("Quicklight_v0.2.0.exe", false)]
    [InlineData("Other_v0.2.0.zip", false)]
    [InlineData("Quicklight_vX.zip", false)]
    public void Release_asset_names(string name, bool ok) => Assert.Equal(ok, Updater.IsReleaseAsset(name));

    [Fact]
    public void Picks_the_versioned_zip()
    {
        var r = Updater.ParseRelease("""
            {"tag_name":"v0.3.0","assets":[{"name":"Quicklight_v0.3.0.zip","browser_download_url":"https://github.com/KusBeoms/Quicklight/releases/download/v0.3.0/Quicklight_v0.3.0.zip","size":5}]}
            """);
        Assert.EndsWith("/Quicklight_v0.3.0.zip", r!.AssetUrl);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("Quicklight 0.10.1", "0.10.1")]
    public void Versions(string tag, string expected) => Assert.Equal(Version.Parse(expected), Updater.ParseVersion(tag));

    [Theory]
    [InlineData("KusBeoms/Quicklight", true)]
    [InlineData("a/b.c-d_e", true)]
    [InlineData("../../etc", false)]
    [InlineData("owner", false)]
    [InlineData("owner/name/extra", false)]
    public void Repository_names(string repo, bool ok) => Assert.Equal(ok, Updater.IsValidRepository(repo));

    static string BuiltExe()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var repo = tfm.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(repo, "src", "Quicklight", "bin", tfm.Parent.Name, tfm.Name, "Quicklight.exe");
    }

    [Fact]
    public void Verify_accepts_Quicklight_and_rejects_other_programs()
    {
        var exe = BuiltExe();
        Updater.Verify(exe, new FileInfo(exe).Length);
        Assert.Throws<UpdateException>(() => Updater.Verify(exe, 1));                                   // size mismatch
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        Assert.Throws<UpdateException>(() => Updater.Verify(notepad, new FileInfo(notepad).Length));   // other product
        var text = Path.GetTempFileName();
        try { File.WriteAllText(text, "hello"); Assert.Throws<UpdateException>(() => Updater.Verify(text, 5)); }
        finally { File.Delete(text); }
    }

    static readonly System.Security.Cryptography.ECDsa TestKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    static string TestPublicKey => TestKey.ExportSubjectPublicKeyInfoPem();

    /// <summary>A release zip like tools\sign-release.ps1 makes: the exe and its base64 signature.</summary>
    static byte[] ReleaseZip(byte[] exe, byte[]? signedBytes = null, System.Security.Cryptography.ECDsa? key = null, bool withSignature = true)
    {
        var sig = Convert.ToBase64String((key ?? TestKey).SignData(signedBytes ?? exe, System.Security.Cryptography.HashAlgorithmName.SHA256));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var e = zip.CreateEntry("Quicklight.exe").Open()) e.Write(exe);
            if (withSignature) using (var w = new StreamWriter(zip.CreateEntry("Quicklight.exe.sig").Open())) w.Write(sig);
        }
        return ms.ToArray();
    }

    static ReleaseInfo Release9(byte[] zip) =>
        new(new Version(9, 0, 0), "v9.0.0", "https://github.com/x/y/releases/download/v9/Quicklight.zip", zip.Length, "", DateTime.UtcNow);

    [Fact]
    public async Task Downloads_verifies_signature_and_swaps()
    {
        var dir = Directory.CreateTempSubdirectory("ql-update-").FullName;
        try
        {
            var current = Path.Combine(dir, "Quicklight.exe");
            File.WriteAllText(current, "old version");
            var exe = await File.ReadAllBytesAsync(BuiltExe());
            var zip = ReleaseZip(exe);
            using var updater = new Updater("KusBeoms/Quicklight", new StubHandler(zip), TestPublicKey);

            double last = 0;
            var downloaded = await updater.DownloadAsync(Release9(zip), current, new Version(0, 0, 1), new SyncProgress(p => last = p), CancellationToken.None);
            Assert.Equal(current + ".new", downloaded);
            Assert.Equal(exe, await File.ReadAllBytesAsync(downloaded));
            Assert.Equal(1.0, last, 3);
            Assert.False(File.Exists(current + ".zip.part")); // the zip is not kept

            var old = Updater.Swap(downloaded, current);
            Assert.Equal(exe.Length, new FileInfo(current).Length);
            Assert.Equal("old version", File.ReadAllText(old));
            await Updater.CleanupAsync(old, current);
            Assert.False(File.Exists(old));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("unsigned")]
    [InlineData("other key")]
    [InlineData("garbage signature")]
    [InlineData("not newer")]
    [InlineData("not a zip")]
    [InlineData("bad key")]
    public async Task Rejects_anything_not_signed_by_the_release_key(string case_)
    {
        var dir = Directory.CreateTempSubdirectory("ql-update-").FullName;
        try
        {
            var current = Path.Combine(dir, "Quicklight.exe");
            File.WriteAllText(current, "old");
            var exe = await File.ReadAllBytesAsync(BuiltExe());
            var tampered = (byte[])exe.Clone();
            tampered[^1] ^= 0xFF;
            var currentVersion = new Version(0, 0, 1);
            byte[] zip = case_ switch
            {
                "tampered" => ReleaseZip(tampered, signedBytes: exe),               // signature is for the original bytes
                "unsigned" => ReleaseZip(exe, withSignature: false),
                "other key" => ReleaseZip(exe, key: System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)),
                "garbage signature" => GarbageSignature(exe),
                "not newer" => ReleaseZip(exe),
                _ => [1, 2, 3, 4],
            };
            if (case_ == "not newer") currentVersion = new Version(99, 0, 0);       // the signed exe is older than what runs
            var publicKey = case_ == "bad key" ? "-----BEGIN PUBLIC KEY-----\nnot a key\n-----END PUBLIC KEY-----" : TestPublicKey;
            if (case_ == "bad key") zip = ReleaseZip(exe);
            using var updater = new Updater("KusBeoms/Quicklight", new StubHandler(zip), publicKey);
            await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(Release9(zip), current, currentVersion, null, CancellationToken.None));
            Assert.False(File.Exists(current + ".new"));
            Assert.False(File.Exists(current + ".zip.part"));
            Assert.Equal("old", File.ReadAllText(current));
        }
        finally { Directory.Delete(dir, true); }
    }

    static byte[] GarbageSignature(byte[] exe)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var e = zip.CreateEntry("Quicklight.exe").Open()) e.Write(exe);
            using (var w = new StreamWriter(zip.CreateEntry("Quicklight.exe.sig").Open())) w.Write("not base64 !!");
        }
        return ms.ToArray();
    }

    [Fact]
    public void The_built_in_release_key_is_a_valid_P256_public_key()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(UpdateSignature.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
        Assert.False(UpdateSignature.Verify([1, 2, 3], Convert.ToBase64String(new byte[64])));
    }

    [Fact]
    public async Task A_truncated_download_is_deleted()
    {
        var dir = Directory.CreateTempSubdirectory("ql-update-").FullName;
        try
        {
            var current = Path.Combine(dir, "Quicklight.exe");
            File.WriteAllText(current, "old");
            using var updater = new Updater("KusBeoms/Quicklight", new StubHandler(new byte[100]), TestPublicKey);
            var release = new ReleaseInfo(new Version(9, 0, 0), "v9", "https://github.com/x", 5000, "", DateTime.UtcNow);
            await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(release, current, new Version(0, 0, 1), null, CancellationToken.None));
            Assert.False(File.Exists(current + ".new"));
            Assert.False(File.Exists(current + ".zip.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Cleanup_removes_every_leftover_old_copy()
    {
        var dir = Directory.CreateTempSubdirectory("ql-update-").FullName;
        try
        {
            var exe = Path.Combine(dir, "Quicklight.exe");
            File.WriteAllText(exe, "current");
            foreach (var n in new[] { "Quicklight.exe.old-a", "Quicklight.exe.old-b", "Quicklight.exe.new" }) File.WriteAllText(Path.Combine(dir, n), "x");
            await Updater.CleanupAsync(null, exe);
            Assert.Equal(["Quicklight.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_new_version_that_will_not_start_is_rolled_back()
    {
        var dir = Directory.CreateTempSubdirectory("ql-update-").FullName;
        try
        {
            var exe = Path.Combine(dir, "Quicklight.exe");
            File.WriteAllText(exe, "working version");
            var downloaded = exe + ".new";
            File.WriteAllText(downloaded, "not a program"); // Process.Start fails on it
            Assert.ThrowsAny<Exception>(() => Updater.ApplyAndRestart(downloaded, exe));
            Assert.Equal("working version", File.ReadAllText(exe));
            Assert.Empty(Directory.GetFiles(dir, "Quicklight.exe.old*"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Cleanup_never_touches_files_outside_the_exe_folder()
    {
        var dir = Directory.CreateTempSubdirectory("ql-update-").FullName;
        var elsewhere = Path.GetTempFileName();
        try
        {
            await Updater.CleanupAsync(elsewhere, Path.Combine(dir, "Quicklight.exe"));
            Assert.True(File.Exists(elsewhere));
        }
        finally { File.Delete(elsewhere); Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("update", true)]
    [InlineData("업데이트", true)]
    [InlineData(" Update ", true)]
    [InlineData("windows update", false)] // that is the Windows settings page
    public async Task Update_row(string query, bool shown)
    {
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null), new AppProvider([]));
        var r = await engine.SearchAsync(query);
        Assert.Equal(shown, r[0].Action == ActionType.Update);
    }

    sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public class BundleTests
{
    [Fact]
    public void Everything_is_bundled_byte_for_byte()
    {
        Assert.True(EverythingBootstrap.HasBundledCopy);
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var repo = tfm.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var vendored = File.ReadAllBytes(Path.Combine(repo, "third_party", "everything", "Everything.exe"));
        Assert.Equal(vendored, EverythingBootstrap.ReadResource("Quicklight.Everything.exe"));
        Assert.Contains("MIT", System.Text.Encoding.UTF8.GetString(EverythingBootstrap.ReadResource("Quicklight.Everything.License.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Translation_install_is_hash_pinned()
    {
        using var s = typeof(TranslationInstaller).Assembly.GetManifestResourceStream("Quicklight.libretranslate-requirements.txt");
        Assert.NotNull(s);
        var lines = new StreamReader(s!).ReadToEnd().Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.Contains(lines, l => l.StartsWith("libretranslate==1.9.6 "));
        Assert.All(lines, l => Assert.Matches(@"^[A-Za-z0-9._-]+==\S+ --hash=sha256:[0-9a-f]{64}$", l));
    }

    [Fact]
    public void Everything_goes_to_program_files_not_the_user_profile() =>
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), EverythingBootstrap.InstallDir);

    [Fact]
    public void Python_package_extraction_rejects_path_traversal()
    {
        var dir = Directory.CreateTempSubdirectory("ql-py-").FullName;
        try
        {
            var zip = Path.Combine(dir, "evil.nupkg");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(archive.CreateEntry("tools/python.exe").Open())) w.Write("MZ");
                using (var w = new StreamWriter(archive.CreateEntry("tools/../../escaped.txt").Open())) w.Write("x");
            }
            Assert.Throws<TranslationException>(() => TranslationInstaller.ExtractTools(zip, Path.Combine(dir, "python")));
            Assert.False(File.Exists(Path.Combine(dir, "..", "escaped.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Installer_paths_and_state()
    {
        var dir = Directory.CreateTempSubdirectory("ql-tr-").FullName;
        try
        {
            var i = new TranslationInstaller(dir);
            Assert.False(i.IsInstalled);
            Assert.Equal(Path.Combine(dir, "python", "Scripts", "libretranslate.exe"), i.ServerExe);
            Assert.Null(LibreTranslateClient.FindServerExe(Path.Combine(dir, "nope")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
