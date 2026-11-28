using System.IO.Compression;
using System.Security.Cryptography;
using Quicklight.Core.Ai;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quicklight.Tests;

public class PdfTextTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ql-pdf-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    string Pdf(params string[] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = b.AddPage(PageSize.A4);
            if (text.Length > 0) page.AddText(text, 12, new PdfPoint(40, 700), font);
        }
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public void Reads_the_text_layer_in_page_order()
    {
        var text = PdfText.Extract(Pdf("Quarterly report for the first page", "Second page talks about the budget"), 12000);
        Assert.NotNull(text);
        Assert.True(text!.IndexOf("Quarterly", StringComparison.Ordinal) < text.IndexOf("budget", StringComparison.Ordinal));
    }

    [Fact]
    public void Blank_pages_go_through_ocr_and_give_nothing() =>
        Assert.Null(PdfText.Extract(Pdf("", ""), 12000));

    [Fact]
    public void Broken_file_is_null()
    {
        var path = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(path, "not a pdf");
        Assert.Null(PdfText.Extract(path, 12000));
    }

    [Fact]
    public void Package_installs_only_with_the_pinned_hash()
    {
        var package = Path.Combine(_dir, "p.nupkg");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("lib/net8.0/UglyToad.PdfPig.dll").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("lib/net6.0/Other.dll").Open())) w.Write("x");
        }
        var target = Path.Combine(_dir, "pdf");
        Assert.Throws<IOException>(() => PdfText.InstallPackage(package, new string('0', 64), target));
        Assert.False(Directory.Exists(target));

        PdfText.InstallPackage(package, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), target);
        Assert.Equal(["UglyToad.PdfPig.dll"], Directory.GetFiles(target).Select(Path.GetFileName));
    }
}
