using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Quicklight.Core.Ai;

/// <summary>
/// PDF text for a summary. PdfPig (Apache-2.0) reads the text layer; it is not inside the exe but downloaded from
/// settings into %LOCALAPPDATA%\Quicklight\ai\pdf (the NuGet package, checked against a pinned SHA-256). A page with
/// no text (a scan) is rendered by Windows.Data.Pdf and read by Windows OCR (Korean, else the profile languages).
/// </summary>
public static class PdfText
{
    public const string Version = "0.1.16";
    // The package's SHA-256, in the code: a replaced package is not installed. Update with Version (and the csproj references).
    const string PinnedSha256 = "D67171846EA8C28F50359137065FEC4514266D7A32B23EAE6C5F2EBED8FFCFC4";
    const string Url = $"https://api.nuget.org/v3-flatcontainer/pdfpig/{Version}/pdfpig.{Version}.nupkg";
    const int MaxOcrPages = 10; // ponytail: ~1 s a page; a long scan is summarized from its first pages
    const int MinPageChars = 20; // fewer letters than this on a page: treated as a scan

    public static string Directory { get; } = Path.Combine(AiModel.Directory, "pdf");
    public static bool IsInstalled => File.Exists(Path.Combine(Directory, "UglyToad.PdfPig.dll"));

    static int _resolver;

    /// <summary>The text of the first pages (up to <paramref name="maxChars"/>), or null when there is none or the file cannot be read.</summary>
    public static string? Extract(string path, int maxChars)
    {
        if (Interlocked.Exchange(ref _resolver, 1) == 0) AssemblyLoadContext.Default.Resolving += Resolve;
        try { return Read(path, maxChars); }
        catch (Exception ex) // a third-party parser on any file: broken, encrypted or odd PDFs throw all kinds
        {
            Log.Error("pdf text failed", ex);
            return null;
        }
    }

    /// <summary>Loads PdfPig from the download folder, from bytes so the files stay deletable while Quicklight runs.</summary>
    static System.Reflection.Assembly? Resolve(AssemblyLoadContext ctx, System.Reflection.AssemblyName name)
    {
        if (name.Name?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) != true) return null;
        var file = Path.Combine(Directory, name.Name + ".dll");
        if (!File.Exists(file)) return null;
        using var s = File.OpenRead(file);
        return ctx.LoadFromStream(s);
    }

    // Separate and not inlined: PdfPig's types are resolved when this is compiled, after the resolver is in place.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string? Read(string path, int maxChars)
    {
        var pages = new List<string?>();
        int chars = 0;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(path))
            foreach (var page in doc.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page);
                bool scan = text.Count(char.IsLetterOrDigit) < MinPageChars;
                pages.Add(scan ? null : text);
                chars += scan ? 0 : text.Length;
                if (chars >= maxChars) break;
            }
        if (pages.Contains(null)) Ocr(path, pages, maxChars - chars);
        var all = string.Join("\n", pages.Where(p => !string.IsNullOrWhiteSpace(p)));
        return all.Length == 0 ? null : all.Length > maxChars ? all[..maxChars] : all;
    }

    /// <summary>Fills the null pages with Windows OCR, in page order, until <paramref name="budget"/> characters.</summary>
    static void Ocr(string path, List<string?> pages, int budget)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ko")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return; // no OCR language installed: scanned pages are skipped
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var doc = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        int done = 0;
        for (int i = 0; i < pages.Count && i < doc.PageCount && done < MaxOcrPages && budget > 0; i++)
        {
            if (pages[i] is not null) continue;
            done++;
            using var page = doc.GetPage((uint)i);
            // ~240 dpi (2.5 x 96), within the largest image the engine takes.
            double scale = Math.Min(2.5, OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
            using var stream = new InMemoryRandomAccessStream();
            page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
            }).AsTask().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            using var bitmap = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            pages[i] = string.Join("\n", result.Lines.Select(l => l.Text));
            budget -= pages[i]!.Length;
        }
    }

    // ---- install ----

    public static async Task InstallAsync(IProgress<(string Label, double? Fraction)> progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(AiModel.Directory);
        var package = Path.Combine(AiModel.Directory, $"pdfpig.{Version}.nupkg");
        await AiModel.DownloadAsync(Url, package, "PDF 읽기 내려받는 중", progress, ct);
        try { InstallPackage(package, PinnedSha256); }
        finally { File.Delete(package); }
    }

    /// <summary>Checks the package's hash and unpacks its net8.0 assemblies into <see cref="Directory"/>.</summary>
    internal static void InstallPackage(string package, string sha256, string? target = null)
    {
        target ??= Directory;
        string actual;
        using (var f = File.OpenRead(package)) actual = Convert.ToHexString(SHA256.HashData(f));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("내려받은 PDF 읽기 파일의 해시가 맞지 않아 지웠습니다. 다시 시도해 주세요.");
        var staging = target + ".new";
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        System.IO.Directory.CreateDirectory(staging);
        using (var zip = ZipFile.OpenRead(package))
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("lib/net8.0/", StringComparison.Ordinal) && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                e.ExtractToFile(Path.Combine(staging, e.Name));
        if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
        System.IO.Directory.Move(staging, target);
    }

    public static void Delete()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }
}
