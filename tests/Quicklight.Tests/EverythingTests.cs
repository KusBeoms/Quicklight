using System.Text;
using Quicklight.Core;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Tests;

public class EverythingTests
{
    static void Str(List<byte> b, string s)
    {
        b.AddRange(BitConverter.GetBytes((uint)s.Length));
        b.AddRange(Encoding.Unicode.GetBytes(s + "\0"));
    }

    [Fact]
    public void Parses_list2_reply()
    {
        // header(20) + 2 items(16) + data
        var data = new List<byte>();
        var item1 = new List<byte>();
        Str(item1, "report.pdf"); Str(item1, @"C:\Docs");
        item1.AddRange(BitConverter.GetBytes(1234L));
        item1.AddRange(BitConverter.GetBytes(new DateTime(2026, 1, 2, 3, 4, 5).ToFileTime()));
        var item2 = new List<byte>();
        Str(item2, "Docs"); Str(item2, @"C:\");
        item2.AddRange(BitConverter.GetBytes(-1L));
        item2.AddRange(BitConverter.GetBytes(0L));

        uint flags = 0x1 | 0x2 | 0x10 | 0x40;
        foreach (var v in new uint[] { 2, 2, 0, flags, 14 }) data.AddRange(BitConverter.GetBytes(v));
        int off1 = 20 + 16, off2 = off1 + item1.Count;
        data.AddRange(BitConverter.GetBytes(0u)); data.AddRange(BitConverter.GetBytes((uint)off1));
        data.AddRange(BitConverter.GetBytes(1u)); data.AddRange(BitConverter.GetBytes((uint)off2));
        data.AddRange(item1); data.AddRange(item2);

        var items = EverythingClient.ParseList2(data.ToArray());
        Assert.Equal(2, items.Count);
        Assert.Equal(@"C:\Docs\report.pdf", items[0].FullPath);
        Assert.Equal(1234, items[0].Size);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), items[0].Modified);
        Assert.False(items[0].IsFolder);
        Assert.True(items[1].IsFolder);
        Assert.Null(items[1].Modified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(19)]
    public void Malformed_replies_do_not_throw(int length)
    {
        var data = new byte[length];
        if (length >= 8) BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(data, 4); // absurd item count
        Assert.Empty(EverythingClient.ParseList2(data));
    }

    [Fact]
    public void Reply_with_offsets_past_the_end_stops_cleanly()
    {
        var data = new List<byte>();
        foreach (var v in new uint[] { 1, 1, 0, 0x3, 1 }) data.AddRange(BitConverter.GetBytes(v));
        data.AddRange(BitConverter.GetBytes(0u)); data.AddRange(BitConverter.GetBytes(28u)); // item points at a string...
        data.AddRange(BitConverter.GetBytes(1_000_000u));                                     // ...claiming 1M chars
        Assert.Empty(EverythingClient.ParseList2(data.ToArray()));
    }

    [Fact]
    public async Task Two_clients_in_one_process_each_get_their_replies()
    {
        if (EverythingClient.GetVersion() is null) return;
        using var a = new EverythingClient(TimeSpan.FromSeconds(30));
        using var b = new EverythingClient(TimeSpan.FromSeconds(30));
        var ra = await a.SearchAsync(new EverythingQuery("startwith:notepad.exe", 3));
        var rb = await b.SearchAsync(new EverythingQuery("startwith:notepad.exe", 3));
        Assert.NotEmpty(ra);
        Assert.NotEmpty(rb);
    }

    /// <summary>Live check against the Everything running on this PC; passes trivially when it is not running or not answering.</summary>
    [Fact]
    public async Task Live_query_finds_windows_notepad()
    {
        if (EverythingClient.GetVersion() is null) return; // Everything not running or hung on this machine
        using var client = new EverythingClient(TimeSpan.FromSeconds(30)); // a huge index can be slow
        var items = await client.SearchAsync(new EverythingQuery("wfn:notepad.exe", 10));
        Assert.Contains(items, i => i.FullPath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Live_engine_returns_files_from_everything()
    {
        if (EverythingClient.GetVersion() is null) return; // Everything not running or hung on this machine
        using var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null), new AppProvider(), new EverythingClient(TimeSpan.FromSeconds(30)), fileTimeout: TimeSpan.FromSeconds(30));
        var r = await engine.SearchAsync("notepad.exe");
        Assert.Contains(r, x => x.Kind == ResultKind.File);
    }

    [Fact]
    public async Task Live_app_index_is_not_empty()
    {
        var apps = await ShellApps.EnumerateAsync();
        Assert.True(apps.Count > 10, $"only {apps.Count} apps");
        Assert.Contains(apps, a => a.FilePath is null); // packaged apps have no file path
    }
}
