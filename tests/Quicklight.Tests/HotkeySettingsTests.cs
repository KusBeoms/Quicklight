using Quicklight.Core;

namespace Quicklight.Tests;

public class HotkeySettingsTests
{
    [Fact]
    public void The_hangul_hotkey_is_off_by_default()
    {
        var s = new QuicklightSettings();
        Assert.False(s.HangulHotkey);
        Assert.Equal("Alt+Space", s.Hotkey);
    }

    [Theory]
    [InlineData("Hangul+Alt+Space", "Alt+Space")]   // how an earlier build wrote it: the hotkey itself is kept
    [InlineData("Alt+Hangul+Space", "Alt+Space")]
    [InlineData("Hangul+Space", "Alt+Space")]        // no modifier left: Alt, so the hotkey can be registered
    [InlineData("hangul+ctrl+k", "ctrl+k")]
    [InlineData("Hangul", "Alt+Space")]
    public void An_old_hangul_hotkey_becomes_the_hotkey_and_the_switch(string written, string hotkey)
    {
        var s = new QuicklightSettings { Hotkey = written };
        s.MoveHangulOutOfHotkey();
        Assert.Equal(hotkey, s.Hotkey);
        Assert.True(s.HangulHotkey);
    }

    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Shift+K")]
    public void Other_hotkeys_are_left_alone(string hotkey)
    {
        var s = new QuicklightSettings { Hotkey = hotkey };
        s.MoveHangulOutOfHotkey();
        Assert.Equal(hotkey, s.Hotkey);
        Assert.False(s.HangulHotkey);
    }

    [Fact]
    public void The_switch_is_saved_and_an_old_file_is_migrated_on_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-settings-{Guid.NewGuid():N}.json");
        try
        {
            new QuicklightSettings { HangulHotkey = true }.Save(path);
            Assert.Contains("\"hangulHotkey\": true", File.ReadAllText(path));
            Assert.True(QuicklightSettings.Load(path).HangulHotkey);

            File.WriteAllText(path, """{ "hotkey": "Hangul+Alt+Space" }""");
            var loaded = QuicklightSettings.Load(path);
            Assert.Equal(("Alt+Space", true), (loaded.Hotkey, loaded.HangulHotkey));

            File.WriteAllText(path, """{ "hotkey": "Alt+Space" }""");
            Assert.False(QuicklightSettings.Load(path).HangulHotkey); // the switch stays off unless it was asked for
        }
        finally { File.Delete(path); }
    }
}
