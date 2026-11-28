using System.Text.Json.Nodes;
using Quicklight.Core.Ai;
using Xunit;

namespace Quicklight.Tests;

public class FileOrganizerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ql-org-" + Guid.NewGuid().ToString("N"));
    readonly FileOrganizerTools _tools;
    readonly Consent _consent = new();

    sealed class Consent : IConsent
    {
        public List<string> Asked = [];
        public Task<bool> ConfirmAsync(string summary, CancellationToken ct) { Asked.Add(summary); return Task.FromResult(true); }
    }

    public FileOrganizerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Downloads"));
        Directory.CreateDirectory(Path.Combine(_root, "AppData", "Local"));
        _tools = new FileOrganizerTools(_consent, _root, Path.Combine(_root, "journal.jsonl"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string F(string rel, string text = "x") { var p = Path.Combine(_root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }

    static JsonObject Ops(params (string op, string from, string to)[] ops) => new()
    {
        ["operations"] = new JsonArray(ops.Select(o => (JsonNode)new JsonObject { ["op"] = o.op, ["from"] = o.from, ["to"] = o.to }).ToArray()),
    };

    [Theory]
    [InlineData(@"relative\a.txt")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows\a.txt")]
    public void Unsafe_paths_are_rejected(string path) => Assert.Throws<ArgumentException>(() => _tools.Check(path));

    [Fact]
    public void Escapes_and_appdata_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _tools.Check(Path.Combine(_root, "Downloads", "..", "..", "x")));
        Assert.Throws<ArgumentException>(() => _tools.Check(Path.Combine(_root, "AppData", "Local", "a.txt")));
        Assert.Throws<ArgumentException>(() => _tools.Check(_root + "-evil\\a.txt")); // prefix match is not "inside"
        Assert.Equal(Path.Combine(_root, "Downloads", "a.txt"), _tools.Check(Path.Combine(_root, "Downloads", "a.txt")));
    }

    [Fact]
    public void Hidden_files_are_rejected()
    {
        var p = F(@"Downloads\h.txt");
        File.SetAttributes(p, FileAttributes.Hidden);
        Assert.Throws<ArgumentException>(() => _tools.Check(p));
    }

    [Fact]
    public void Clash_naming_appends_counter()
    {
        var a = F(@"Downloads\a.pdf"); F(@"Downloads\a (2).pdf");
        Assert.Equal(Path.Combine(_root, "Downloads", "a (3).pdf"), FileOrganizerTools.FreeName(a, File.Exists));
    }

    [Fact]
    public void Plan_clashes_inside_one_batch_and_rejects_bad_batch()
    {
        var a = F(@"Downloads\a.txt"); var b = F(@"Downloads\sub\a.txt"); var docs = Path.Combine(_root, "Downloads", "문서");
        var (moves, dirs) = _tools.Plan([new("mkdir", "", docs), new("move", a, docs), new("move", b, docs)]);
        Assert.Equal([Path.Combine(docs, "a.txt"), Path.Combine(docs, "a (2).txt")], moves.Select(m => m.To));
        Assert.Single(dirs);
        Assert.Throws<ArgumentException>(() => _tools.Plan([new("move", a, docs), new("move", a, @"C:\Windows\a.txt")]));
        Assert.Throws<ArgumentException>(() => _tools.Plan([.. Enumerable.Range(0, 1001).Select(_ => new FileOrganizerTools.Op("mkdir", "", docs))]));
    }

    [Fact]
    public async Task Organize_then_undo_restores_everything_without_overwriting()
    {
        var a = F(@"Downloads\a.pdf", "A"); var b = F(@"Downloads\b.pdf", "B");
        var docs = Path.Combine(_root, "Downloads", "문서"); F(@"Downloads\문서\a.pdf", "OLD");
        var r = await _tools.CallAsync("organize", Ops(("mkdir", "", docs), ("move", a, docs), ("move", b, Path.Combine(docs, "b.pdf"))), default);
        Assert.Contains("2개를 옮겼습니다", r);
        Assert.Single(_consent.Asked);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(docs, "a.pdf")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(docs, "a (2).pdf")));
        Assert.False(File.Exists(a));

        File.WriteAllText(a, "NEW"); // the source path is taken again: a's undo must not overwrite it
        var u = await _tools.CallAsync("undo_last_organize", [], default);
        Assert.Equal("NEW", File.ReadAllText(a));
        Assert.True(File.Exists(Path.Combine(docs, "a (2).pdf")));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.Contains("1개", u);
        Assert.Contains("되돌릴 정리가 없습니다", await _tools.CallAsync("undo_last_organize", [], default));
    }

    [Fact]
    public async Task Invalid_batch_changes_nothing_and_asks_nothing()
    {
        var a = F(@"Downloads\a.txt");
        var r = await _tools.CallAsync("organize", Ops(("move", a, Path.Combine(_root, "Downloads", "x", "a.txt")), ("move", a, @"C:\Windows\a.txt")), default);
        Assert.Contains("오류", r);
        Assert.Empty(_consent.Asked);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task List_folder_caps_entries()
    {
        for (int i = 0; i < 510; i++) F($@"Downloads\f{i}.txt");
        var r = await _tools.CallAsync("list_folder", new JsonObject { ["path"] = Path.Combine(_root, "Downloads") }, default);
        Assert.Contains("처음 500개만", r);
    }
}
