using System.IO.Compression;
using Quicklight.Core.Ai;
using Xunit;

namespace Quicklight.Tests;

public class DocumentTextTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ql-doctext-" + Guid.NewGuid().ToString("N"));

    public DocumentTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    static void Zip(string path, params (string Name, string Body)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(body);
        }
    }

    [Fact]
    public void ReadsDocxParagraphsWithoutTags()
    {
        string p = Path.Combine(_dir, "a.docx");
        Zip(p, ("word/document.xml", "<w:document><w:p><w:r><w:t>첫 문단 &amp; 끝</w:t></w:r></w:p><w:p><w:r><w:t>둘째</w:t></w:r></w:p></w:document>"));
        Assert.Equal("첫 문단 & 끝\n둘째", DocumentText.Extract(p)!.Trim());
    }

    [Fact]
    public void ReadsPptxSlidesInOrder()
    {
        string p = Path.Combine(_dir, "a.pptx");
        Zip(p, ("ppt/slides/slide10.xml", "<a:p><a:t>열번째</a:t></a:p>"), ("ppt/slides/slide2.xml", "<a:p><a:t>둘째</a:t></a:p>"));
        string text = DocumentText.Extract(p)!;
        Assert.True(text.IndexOf("둘째", StringComparison.Ordinal) < text.IndexOf("열번째", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsTextFileUpToLimit()
    {
        string p = Path.Combine(_dir, "a.txt");
        File.WriteAllText(p, new string('가', DocumentText.MaxChars + 500));
        Assert.Equal(DocumentText.MaxChars, DocumentText.Extract(p)!.Length);
    }

    [Fact]
    public void UnsupportedFileHasNoText()
    {
        string p = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(p, [1, 2, 3]);
        Assert.False(DocumentText.Supports(p));
        Assert.Null(DocumentText.Extract(p));
    }
}
