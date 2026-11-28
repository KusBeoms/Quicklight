using System.Text.Encodings.Web;
using System.Text.Json;

namespace Quicklight.Core.Ai;

/// <summary>The AI actions on a clipboard text entry (Tab in the clipboard scope): the list, the prompt of each, and the
/// JSON pretty-print that needs no model.</summary>
public static class ClipboardActions
{
    public const int MaxInput = 8000; // characters sent to the model; the rest is cut and the card says so

    /// <param name="Id">Stable id, the row's target.</param>
    /// <param name="Label">Korean name, the row's title.</param>
    /// <param name="Task">The prompt; the clipboard text goes in as data and the model answers with the result only.</param>
    public sealed record Item(string Id, string Label, Prompts.OneShot Task);

    public static readonly IReadOnlyList<Item> All =
    [
        new("summary", "요약", Prompts.ClipSummary),
        new("polish", "다듬기", Prompts.Polish),
        new("en", "영어로 번역", Prompts.Translate("English")),
        new("ko", "한국어로 번역", Prompts.Translate("Korean")),
        new("csv", "표를 CSV로", Prompts.Csv),
        new("json", "JSON 정리", Prompts.Json),
    ];

    public static Item? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>The result without the model, or null when the model is needed. JSON that parses is pretty-printed exactly.</summary>
    public static string? Local(string id, string text) => id == "json" ? PrettyJson(text) : null;

    /// <summary>The text as indented JSON, or null when it is not JSON. Hangul stays as written.</summary>
    public static string? PrettyJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The text cut to <see cref="MaxInput"/> characters, and whether it was cut.</summary>
    public static (string Text, bool Truncated) Clip(string text) =>
        text.Length <= MaxInput ? (text, false) : (text[..MaxInput], true);
}
