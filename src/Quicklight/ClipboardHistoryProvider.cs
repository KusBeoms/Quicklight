using System.Windows.Media;
using System.Windows.Media.Imaging;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Quicklight;

/// <summary>
/// "clip", "클립보드", "ㅋㄹ" (+ words to filter): Windows' own clipboard history, the same list as Win+V, newest first.
/// Enter makes the entry current and pastes it (the launcher does that); Delete removes it from the history.
/// </summary>
public sealed class ClipboardHistoryProvider : IResultProvider
{
    static readonly string[] Prefixes = ["clip", "클립보드", "ㅋㄹ", "cb"];

    // Entries of the last listing by id, for paste/delete, and image thumbnails for the rows.
    readonly Dictionary<string, ClipboardHistoryItem> _items = [];
    readonly Dictionary<string, ImageSource> _thumbnails = [];

    public string Name => "clipboard";

    public async Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        // A whole word only: "clipchamp" is an app, not the clipboard.
        var prefix = Prefixes.FirstOrDefault(p => query.Text.Equals(p, StringComparison.OrdinalIgnoreCase)
                                                  || query.Text.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return [];
        var filter = query.Text[prefix.Length..].Trim();

        if (!WinClipboard.IsHistoryEnabled())
            return
            [
                new SearchResult
                {
                    Title = "클립보드 기록이 꺼져 있습니다",
                    Subtitle = "Enter로 설정 열기 · 켠 뒤 복사한 항목부터 기록됩니다",
                    Kind = ResultKind.Clipboard,
                    Target = "ms-settings:clipboard",
                    Score = Scores.Clipboard + 100,
                },
            ];

        var history = await WinClipboard.GetHistoryItemsAsync();
        if (history.Status != ClipboardHistoryItemsResultStatus.Success) return [];

        var results = new List<SearchResult>();
        int i = 0;
        foreach (var item in history.Items)
        {
            ct.ThrowIfCancellationRequested();
            var content = item.Content;
            string title, subtitle = $"클립보드 · {item.Timestamp.LocalDateTime:M월 d일 HH:mm}";
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (filter.Length > 0 && !text.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                title = SnippetProvider.OneLine(text);
            }
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                if (filter.Length > 0) continue;
                title = "이미지";
                if (!_thumbnails.ContainsKey(item.Id) && await LoadThumbnailAsync(content) is { } thumb) _thumbnails[item.Id] = thumb;
            }
            else continue; // files and HTML-only entries: nothing to show in one row

            _items[item.Id] = item;
            results.Add(new SearchResult
            {
                Title = title.Length > 200 ? title[..200] + "…" : title,
                Subtitle = subtitle,
                Kind = ResultKind.Clipboard,
                Target = item.Id,
                Action = ActionType.Paste,
                Score = Scores.Clipboard + 50 - i++, // newest first, like Win+V
            });
        }
        return results;
    }

    static async Task<ImageSource?> LoadThumbnailAsync(DataPackageView content)
    {
        try
        {
            var reference = await content.GetBitmapAsync();
            using var stream = (await reference.OpenReadAsync()).AsStreamForRead();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) { Core.Log.Error("clipboard thumbnail", ex); return null; }
    }

    public ImageSource? Thumbnail(string id) => _thumbnails.GetValueOrDefault(id);

    /// <summary>Makes the entry the current clipboard content. False if it is gone from the history.</summary>
    public bool MakeCurrent(string id) =>
        _items.TryGetValue(id, out var item) && WinClipboard.SetHistoryItemAsContent(item) == SetHistoryItemAsContentStatus.Success;

    public bool Delete(string id) => _items.Remove(id, out var item) && WinClipboard.DeleteItemFromHistory(item);
}
