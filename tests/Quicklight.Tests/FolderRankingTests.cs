using Quicklight.Core;
using Quicklight.Core.Models;
using Xunit;

namespace Quicklight.Tests;

public class FolderRankingTests
{
    static SearchResult R(ResultKind kind, double score, string name, bool nameMatch = true) =>
        new() { Title = name, Kind = kind, Target = @"C:\" + name, Score = score, NameMatch = nameMatch };

    static readonly SearchResult Web = new() { Title = "web", Kind = ResultKind.WebSearch, Target = "https://www.google.com/search?q=x" };

    [Fact]
    public void Matching_folders_keep_slots_when_apps_and_files_outrank_them()
    {
        var candidates = new List<SearchResult>();
        for (int i = 0; i < 10; i++) candidates.Add(R(ResultKind.App, 100 - i, "app" + i));
        for (int i = 0; i < 10; i++) candidates.Add(R(ResultKind.File, 80 - i, "file" + i));
        candidates.Add(R(ResultKind.Folder, 50, "Downloads"));
        candidates.Add(R(ResultKind.Folder, 49, "Downloads2"));

        var ranked = SearchEngine.Rank(candidates, Web, 12, new QuicklightSettings());

        Assert.Equal(12, ranked.Count);
        Assert.Contains(ranked, r => r.Title == "Downloads");
        Assert.Contains(ranked, r => r.Title == "Downloads2");
        Assert.Equal("app0", ranked[0].Title);                 // the top hit is still the best result
        Assert.Equal(ResultKind.WebSearch, ranked[^1].Kind);   // web search stays last
    }

    [Fact]
    public void Folders_matched_only_by_path_get_no_reserved_slot()
    {
        var candidates = new List<SearchResult>();
        for (int i = 0; i < 15; i++) candidates.Add(R(ResultKind.App, 100 - i, "app" + i));
        candidates.Add(R(ResultKind.Folder, 50, "elsewhere", nameMatch: false));

        var ranked = SearchEngine.Rank(candidates, Web, 12, new QuicklightSettings());

        Assert.DoesNotContain(ranked, r => r.Kind == ResultKind.Folder);
    }

    [Fact]
    public void Files_and_folders_are_capped_separately()
    {
        var settings = new QuicklightSettings { MaxFileResults = 2, MaxFolderResults = 2 };
        var candidates = new List<SearchResult>();
        for (int i = 0; i < 5; i++) candidates.Add(R(ResultKind.File, 90 - i, "file" + i));
        for (int i = 0; i < 5; i++) candidates.Add(R(ResultKind.Folder, 80 - i, "dir" + i));

        var ranked = SearchEngine.Rank(candidates, Web, 12, settings);

        Assert.Equal(2, ranked.Count(r => r.Kind == ResultKind.File));
        Assert.Equal(2, ranked.Count(r => r.Kind == ResultKind.Folder));
        // Score order is kept: the reserved folders are not moved above the better files.
        Assert.Equal(["file0", "file1", "dir0", "dir1", "web"], ranked.Select(r => r.Title));
    }
}
