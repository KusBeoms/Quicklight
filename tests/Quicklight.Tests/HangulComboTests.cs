using Quicklight.Core;

namespace Quicklight.Tests;

/// <summary>The 한/영 key must keep doing its own job; "Hangul+Space" only takes it when Space comes while it is held.</summary>
public class HangulComboTests
{
    const uint Hangul = HangulCombo.Hangul, Space = 0x20, A = 0x41, Ctrl = 0x11, Shift = 0x10, Scan = 0xF2;

    /// <summary>Feeds keys in and records what happened: what was swallowed, what was sent on, how often it fired.</summary>
    sealed class Rig
    {
        public readonly List<string> Sent = [];
        public int Fired;
        public bool ModifiersMatch = true;
        public long Now = 1000;
        public readonly HangulCombo Combo;

        public Rig() => Combo = new(Space, () => ModifiersMatch, (scan, down) => Sent.Add(down ? $"hangul-down:{scan:X}" : "hangul-up"), () => Fired++, () => Now);

        public bool Down(uint vk) => Combo.Handle(vk, vk == Hangul ? Scan : 0, down: true);
        public bool Up(uint vk) => Combo.Handle(vk, vk == Hangul ? Scan : 0, down: false);
    }

    [Fact]
    public void A_tap_of_the_key_toggles_as_before_when_released()
    {
        var r = new Rig();
        Assert.True(r.Down(Hangul));
        Assert.Empty(r.Sent); // held back until it is clear what it is for
        Assert.True(r.Up(Hangul));
        Assert.Equal(["hangul-down:F2", "hangul-up"], r.Sent); // the very key, with its scan code
        Assert.Equal(0, r.Fired);
    }

    [Fact]
    public void The_hotkey_fires_once_and_neither_key_reaches_the_system()
    {
        var r = new Rig();
        Assert.True(r.Down(Hangul));
        Assert.True(r.Down(Space));
        Assert.Equal(1, r.Fired);
        Assert.True(r.Up(Space));
        Assert.True(r.Up(Hangul));
        Assert.Empty(r.Sent); // the input mode does not change
    }

    [Fact]
    public void Auto_repeat_of_either_key_does_not_fire_again()
    {
        var r = new Rig();
        r.Down(Hangul);
        r.Down(Space);
        for (int i = 0; i < 5; i++) { Assert.True(r.Down(Hangul)); Assert.True(r.Down(Space)); }
        Assert.Equal(1, r.Fired);
        r.Up(Space);
        r.Down(Space); // pressed again while still holding Hangul
        Assert.Equal(2, r.Fired);
    }

    [Fact]
    public void Typing_right_after_a_toggle_keeps_the_order_hangul_then_the_key()
    {
        var r = new Rig();
        Assert.True(r.Down(Hangul));
        Assert.False(r.Down(A)); // the key itself goes through...
        Assert.Equal(["hangul-down:F2"], r.Sent); // ...after the 한/영 key was sent on
        Assert.False(r.Up(A));
        Assert.True(r.Up(Hangul));
        Assert.Equal(["hangul-down:F2", "hangul-up"], r.Sent); // one toggle, released once
        Assert.Equal(0, r.Fired);
    }

    [Fact]
    public void Other_keys_and_the_key_alone_are_untouched_when_it_is_not_held()
    {
        var r = new Rig();
        Assert.False(r.Down(Space)); // a plain Space is not the hotkey
        Assert.False(r.Up(Space));
        Assert.False(r.Down(A));
        Assert.False(r.Up(A));
        Assert.False(r.Up(Hangul)); // went down before the hook was there: let the release through
        Assert.Empty(r.Sent);
        Assert.Equal(0, r.Fired);
    }

    [Fact]
    public void A_modifier_pressed_after_the_key_does_not_count_as_typing()
    {
        var r = new Rig { ModifiersMatch = false };
        r.Down(Hangul);
        Assert.False(r.Down(Ctrl));
        Assert.Empty(r.Sent);
        r.ModifiersMatch = true; // Ctrl+Hangul+Space
        Assert.True(r.Down(Space));
        Assert.Equal(1, r.Fired);
        r.Up(Space);
        r.Up(Ctrl);
        r.Up(Hangul);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Space_with_the_wrong_modifiers_is_just_typing()
    {
        var r = new Rig { ModifiersMatch = false }; // e.g. Shift is held but the hotkey is Hangul+Space
        r.Down(Hangul);
        Assert.False(r.Down(Shift));
        Assert.False(r.Down(Space));
        Assert.Equal(["hangul-down:F2"], r.Sent);
        Assert.Equal(0, r.Fired);
    }

    [Fact]
    public void A_release_that_never_came_does_not_hold_keys_back_forever()
    {
        var r = new Rig();
        r.Down(Hangul);
        r.Now += 5000; // the screen was locked, the release went elsewhere
        Assert.False(r.Down(A)); // not held any more: the key is plain
        Assert.Empty(r.Sent);
        Assert.False(r.Down(Space));
        Assert.Equal(0, r.Fired);
        Assert.False(r.Up(Hangul)); // and its late release goes through
    }

    [Fact]
    public void Reset_forgets_a_held_key()
    {
        var r = new Rig();
        r.Down(Hangul);
        r.Combo.Reset();
        Assert.False(r.Down(Space));
        Assert.Equal(0, r.Fired);
        Assert.False(r.Up(Hangul));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Every_toggle_in_a_session_is_kept_exactly_once()
    {
        var r = new Rig();
        for (int i = 0; i < 3; i++) { r.Down(Hangul); r.Up(Hangul); } // three taps
        r.Down(Hangul); r.Down(Space); r.Up(Space); r.Up(Hangul);      // one hotkey
        r.Down(Hangul); r.Down(A); r.Up(A); r.Up(Hangul);              // one toggle while typing
        Assert.Equal(1, r.Fired);
        Assert.Equal(4, r.Sent.Count(s => s.StartsWith("hangul-down"))); // 3 taps + 1 typing: 4 toggles, none lost, none doubled
        Assert.Equal(4, r.Sent.Count(s => s == "hangul-up"));
    }
}
