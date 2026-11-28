using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Quicklight.Core.Models;

namespace Quicklight.Core.Ai;

/// <summary>The text of a file for a summary: plain text, Office Open XML (docx, pptx, xlsx), HWPX (zipped XML) and, once PDF reading is installed, PDF. Null when there is none.</summary>
public static class DocumentText
{
    public const int MaxChars = 12000;
    static readonly string[] Zipped = [".docx", ".pptx", ".xlsx", ".hwpx"];

    public static bool Supports(string path) =>
        FileTypeLabel.IsPlainText(path) || Array.IndexOf(Zipped, Path.GetExtension(path).ToLowerInvariant()) >= 0 || IsPdf(path) && PdfText.IsInstalled;

    static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static string? Extract(string path)
    {
        try
        {
            if (FileTypeLabel.IsPlainText(path)) return Usable(ReadText(path));
            if (!Supports(path)) return null;
            if (IsPdf(path)) return Usable(PdfText.Extract(path, MaxChars) ?? "");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using var zip = ZipFile.OpenRead(path);
            var sb = new StringBuilder();
            // ponytail: slides and sections sorted by name length (slide2 before slide10); good enough for a summary.
            foreach (var entry in zip.Entries.Where(e => Wanted(ext, e.FullName)).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                sb.Append(StripXml(reader.ReadToEnd())).Append('\n');
                if (sb.Length >= MaxChars) break;
            }
            return Usable(sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    static bool Wanted(string ext, string name) => ext switch
    {
        ".docx" => name == "word/document.xml",
        ".pptx" => Regex.IsMatch(name, @"^ppt/slides/slide\d+\.xml$"),
        ".xlsx" => name == "xl/sharedStrings.xml",
        _ => Regex.IsMatch(name, @"^Contents/section\d+\.xml$"),
    };

    // ponytail: UTF-8 (or BOM) only; a CP949 text file comes out garbled. Add the fallback from ReadTextHead when it matters.
    static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars];
        int n = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, n);
    }

    static string StripXml(string xml)
    {
        string s = Regex.Replace(xml, @"</(w:p|a:p|hp:p|row|si)>", "\n");
        s = Regex.Replace(s, "<[^>]+>", "");
        return WebUtility.HtmlDecode(s);
    }

    static string? Usable(string text)
    {
        string t = text.Length > MaxChars ? text[..MaxChars] : text;
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }
}
