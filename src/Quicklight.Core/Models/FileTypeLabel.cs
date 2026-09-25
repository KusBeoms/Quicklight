namespace Quicklight.Core.Models;

/// <summary>Subtitle for a file result: what it is ("영상 (mp4)"), not where it is (that is what reveal-in-explorer is for).</summary>
public static class FileTypeLabel
{
    static readonly Dictionary<string, string[]> Categories = new()
    {
        ["문서"] = ["txt", "md", "doc", "docx", "pdf", "hwp", "hwpx", "rtf", "odt", "csv", "log"],
        ["스프레드시트"] = ["xls", "xlsx", "xlsm", "ods"],
        ["프레젠테이션"] = ["ppt", "pptx", "odp", "key"],
        ["영상"] = ["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "ts"],
        ["음악"] = ["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma"],
        ["이미지"] = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "heic", "tiff", "psd"],
        ["압축 파일"] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso"],
        ["애플리케이션"] = ["exe", "msi", "lnk", "bat", "cmd", "appx", "msix"],
        ["코드"] = ["cs", "js", "jsx", "tsx", "py", "java", "cpp", "c", "h", "go", "rs", "html", "css", "json", "xml", "yml", "yaml", "sql", "ps1", "sh"],
    };

    static readonly Dictionary<string, string> ByExtension =
        Categories.SelectMany(c => c.Value.Select(e => (e, c.Key))).ToDictionary(x => x.e, x => x.Key);

    static readonly HashSet<string> TextExtensions = [.. Categories["코드"], "txt", "md", "csv", "log", "ini", "cfg", "toml"];
    static readonly HashSet<string> ImageExtensions = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "tiff"];

    static string Ext(string name) => Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

    /// <summary>Readable as plain text in a preview.</summary>
    public static bool IsPlainText(string name) => TextExtensions.Contains(Ext(name));

    /// <summary>Decodable by WPF for a full-size preview.</summary>
    public static bool IsImage(string name) => ImageExtensions.Contains(Ext(name));

    public static string For(string name, bool isFolder)
    {
        if (isFolder) return "폴더";
        var ext = Ext(name);
        if (ext.Length == 0) return "파일";
        return ByExtension.TryGetValue(ext, out var cat) ? $"{cat} ({ext})" : $"파일 ({ext})";
    }
}
