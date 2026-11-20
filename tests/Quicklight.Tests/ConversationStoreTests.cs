using Quicklight.Core.Ai;

namespace Quicklight.Tests;

public class ConversationStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ql-conv-").FullName;
    string PathOf => Path.Combine(_dir, "conversations.json");

    public void Dispose() => Directory.Delete(_dir, true);

    static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };

    [Fact]
    public void A_new_conversation_is_not_listed_until_it_has_a_message()
    {
        var store = new ConversationStore(null);
        var c = store.Create();
        Assert.Empty(store.All);
        Assert.Same(c, store.Get(c.Id));
        store.Add(c, User("안녕"));
        Assert.Same(c, Assert.Single(store.All));
    }

    [Fact]
    public void The_first_user_message_becomes_the_title()
    {
        var store = new ConversationStore(null);
        var c = store.Create();
        store.Add(c, User("  첫 줄입니다\n둘째 줄"));
        Assert.Equal("첫 줄입니다", c.Title);
        store.Add(c, User("다른 질문"));
        Assert.Equal("첫 줄입니다", c.Title); // only while empty

        var longOne = store.Create();
        store.Add(longOne, User(new string('가', 100)));
        Assert.True(longOne.Title.Length <= 31);
        Assert.EndsWith("…", longOne.Title);

        var assistantFirst = store.Create();
        store.Add(assistantFirst, new ChatMessage { Role = ChatRole.Assistant, Text = "답" });
        Assert.Equal("", assistantFirst.Title);
    }

    [Fact]
    public void Rename_pin_and_order()
    {
        var store = new ConversationStore(null);
        var a = store.Create(); store.Add(a, User("a"));
        var b = store.Create(); store.Add(b, User("b"));
        var c = store.Create(); store.Add(c, User("c"));
        a.Updated = DateTime.Now.AddHours(-3); b.Updated = DateTime.Now.AddHours(-2); c.Updated = DateTime.Now.AddHours(-1);
        Assert.Equal([c.Id, b.Id, a.Id], store.All.Select(x => x.Id));

        store.SetPinned(a.Id, true);
        Assert.Equal([a.Id, c.Id, b.Id], store.All.Select(x => x.Id)); // pinned first, then newest

        store.Rename(b.Id, "  새 이름 ");
        Assert.Equal("새 이름", b.Title);
    }

    [Fact]
    public void Delete_returns_how_many_went()
    {
        var store = new ConversationStore(null);
        var a = store.Create(); store.Add(a, User("a"));
        var b = store.Create(); store.Add(b, User("b"));
        Assert.Equal(1, store.Delete([a.Id, "nope"]));
        Assert.Null(store.Get(a.Id));
        Assert.Same(b, Assert.Single(store.All));
        Assert.Equal(0, store.Delete(["nope"]));
    }

    [Fact]
    public void Purge_removes_old_conversations_but_not_pinned_ones()
    {
        var store = new ConversationStore(null);
        var old = store.Create(); store.Add(old, User("old"));
        var oldPinned = store.Create(); store.Add(oldPinned, User("pinned"));
        var fresh = store.Create(); store.Add(fresh, User("fresh"));
        old.Updated = oldPinned.Updated = DateTime.Now.AddDays(-40);
        store.SetPinned(oldPinned.Id, true);

        Assert.Equal(1, store.PurgeOlderThan(TimeSpan.FromDays(30)));
        Assert.Equal(new[] { oldPinned.Id, fresh.Id }.Order(), store.All.Select(x => x.Id).Order());
    }

    [Fact]
    public void Changed_fires_after_each_mutation()
    {
        var store = new ConversationStore(null);
        int n = 0;
        store.Changed += () => n++;
        var c = store.Create();
        store.Add(c, User("x"));
        store.Rename(c.Id, "y");
        store.SetPinned(c.Id, true);
        store.Delete([c.Id]);
        Assert.Equal(4, n);
    }

    [Fact]
    public void Reloads_from_the_file()
    {
        var store = new ConversationStore(PathOf);
        var c = store.Create();
        store.Add(c, new ChatMessage { Role = ChatRole.User, Text = "안녕", Attachments = [new Attachment("image", @"C:\a.png", "a.png")] });
        store.Add(c, new ChatMessage { Role = ChatRole.Assistant, Text = "반갑습니다", IsError = true });
        store.SetPinned(c.Id, true);
        store.Flush();

        var again = new ConversationStore(PathOf);
        var loaded = Assert.Single(again.All);
        Assert.Equal(c.Id, loaded.Id);
        Assert.Equal("안녕", loaded.Title);
        Assert.True(loaded.Pinned);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal(new Attachment("image", @"C:\a.png", "a.png"), loaded.Messages[0].Attachments[0]);
        Assert.True(loaded.Messages[1].IsError);
        Assert.Equal(ChatRole.Assistant, loaded.Messages[1].Role);
    }

    [Fact]
    public void Empty_conversations_are_not_saved()
    {
        var store = new ConversationStore(PathOf);
        store.Create();
        store.Flush();
        Assert.Empty(new ConversationStore(PathOf).All);
        if (File.Exists(PathOf)) Assert.DoesNotContain("\"Id\"", File.ReadAllText(PathOf));
    }

    [Fact]
    public void A_corrupt_file_starts_empty_and_is_kept_as_bak()
    {
        File.WriteAllText(PathOf, "{ not json");
        var store = new ConversationStore(PathOf);
        Assert.Empty(store.All);
        Assert.Equal("{ not json", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(PathOf)!, Path.GetFileName(PathOf) + ".*.bak")))); // timestamped, so a later bad file cannot overwrite it

        var c = store.Create();
        store.Add(c, User("x"));
        store.Flush();
        Assert.Single(new ConversationStore(PathOf).All);
    }

    [Fact]
    public void Saves_without_a_flush_after_the_delay()
    {
        var store = new ConversationStore(PathOf);
        store.Add(store.Create(), User("x"));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(PathOf) && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.True(File.Exists(PathOf));
    }
}
