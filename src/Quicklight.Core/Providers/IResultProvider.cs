using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>How much file search a query should do: none, the fast name-prefix pass, or everything.</summary>
public enum FileStage { None, Prefix, Full }

/// <param name="Text">Trimmed query as typed, or its keyboard-layout conversion when <paramref name="IsAlternate"/>.</param>
/// <param name="IsAlternate">True for the Korean/English layout-converted retry; providers then require a strong match.</param>
/// <param name="ExpandSystemFolders">Show system (hidden/system-attribute) folders instead of the "시스템 폴더" placeholder.</param>
/// <param name="ExpandSystemFiles">Show system (hidden/system-attribute) files instead of the "시스템 파일" placeholder.</param>
public sealed record QueryContext(string Text, bool IsAlternate = false, FileStage Files = FileStage.Full,
    bool ExpandSystemFolders = false, bool ExpandSystemFiles = false)
{
    /// <summary>Minimum match score a provider should accept for fuzzy name matches.</summary>
    public double MinMatch => IsAlternate ? 72 : 50;
}

public interface IResultProvider
{
    string Name { get; }

    /// <summary>Scores are absolute: see <see cref="Scores"/> for the scale shared by every provider.</summary>
    Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct);
}

/// <summary>Score bands. Match scores (0-100) from FuzzyMatcher are added on top of a kind's base.</summary>
public static class Scores
{
    public const double Calculator = 1000;
    public const double Translation = 980;
    public const double TranslationPending = 60;
    public const double Currency = 950;
    public const double ExactPath = 900;
    public const double PathCompletion = 850;
    public const double ExplicitUrl = 800;
    public const double BareDomain = 300;
    public const double AppBase = 100;
    public const double SettingBase = 80;
    public const double CommandBase = 80;
    public const double FileBase = 40;
    public const double WebSearch = 0;
}
