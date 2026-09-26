using System.ComponentModel;
using System.Windows.Media;
using Quicklight.Core.Models;

namespace Quicklight;

/// <summary>A result row. <see cref="Group"/> drives the Spotlight-style section headers.</summary>
public sealed class ResultItem(SearchResult result, string group) : INotifyPropertyChanged
{
    public SearchResult Result { get; } = result;
    public string Group { get; } = group;
    public string Title => Result.Title;
    public string Subtitle => _subtitleOverride ?? Result.Subtitle;
    public string Glyph => GlyphFor(Result);
    /// <summary>A direct answer on top (calculation or conversion) is drawn as a large card.</summary>
    public bool IsHero => Group == HeroGroup && Result.Kind is ResultKind.Calculator or ResultKind.Currency
                          && Result.Action != ActionType.None;

    public const string HeroGroup = "최상위 결과";

    public double HeroFontSize => 34;

    /// <summary>What Enter does on the card.</summary>
    public string HeroHint => "↵ 복사";

    string? _subtitleOverride;
    ImageSource? _icon;

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Changed(nameof(Icon)); Changed(nameof(HasIcon)); }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Holding Space opens a preview: files, folders, apps (their details) and clipboard entries. The row shows a ›.</summary>
    public bool HasPreview => CanPreview(Result);

    public static bool CanPreview(SearchResult r) =>
        r.App is not null || r.RevealPath is not null && r.Kind is ResultKind.File or ResultKind.Folder or ResultKind.Path
        || r.Kind == ResultKind.Clipboard && r.Action == ActionType.Paste;

    /// <summary>A clipboard entry's text for its card (the first lines); null for an image, which shows <see cref="Icon"/>.</summary>
    public string? Body { get; init; }

    /// <summary>Drawn as a large card: text or image from the clipboard history.</summary>
    public bool IsClip => Result.Kind == ResultKind.Clipboard && Result.Action == ActionType.Paste;

    /// <summary>Shown instead of the subtitle while a destructive command waits for its second Enter.</summary>
    public void SetSubtitleOverride(string? text)
    {
        _subtitleOverride = text;
        Changed(nameof(Subtitle));
    }

    public static string KindLabel(ResultKind k) => k switch
    {
        ResultKind.Calculator => "계산기",
        ResultKind.Currency => "환율",
        ResultKind.Url => "웹사이트",
        ResultKind.Path => "경로",
        ResultKind.App => "앱",
        ResultKind.Setting => "설정",
        ResultKind.Command => "명령",
        ResultKind.Folder => "폴더",
        ResultKind.File => "문서",
        ResultKind.WebSearch => "웹 검색",
        ResultKind.Window => "열린 창",
        ResultKind.Clipboard => "클립보드",
        ResultKind.Snippet => "스니펫",
        ResultKind.Process => "프로세스",
        _ => k.ToString(),
    };

    static string GlyphFor(SearchResult r) => r.Kind switch
    {
        ResultKind.Calculator => "",
        ResultKind.Currency => "",
        ResultKind.Url => "",
        ResultKind.WebSearch => "",
        ResultKind.Setting => "",
        ResultKind.Folder => "",
        ResultKind.File => "",
        ResultKind.Path => "",
        ResultKind.App => "",
        ResultKind.Window => "",
        ResultKind.Clipboard => "",
        ResultKind.Snippet => "",
        ResultKind.Process => "",
        ResultKind.Command => r.Target switch
        {
            "lock" => "",
            "sleep" or "hibernate" => "",
            "restart" => "",
            "signout" => "",
            "recyclebin" => "",
            _ => "",
        },
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
