namespace Quicklight.Core;

/// <summary>
/// "Hangul+Space": the 한/영 key held while another key is pressed. Windows cannot register that as a hotkey, so the
/// keyboard hook feeds every key here. The 한/영 key must keep doing its own job, so it is held back only until it is
/// clear what it was for:
/// <list type="bullet">
/// <item>Released alone: it was a tap, and is sent on again (down and up) at the release.</item>
/// <item>Another key is pressed while it is held (typing right after a toggle): it is sent on first, then the key goes through.</item>
/// <item>The trigger key is pressed while it is held: that is the hotkey. Neither key reaches the system, so the input
/// mode does not change.</item>
/// </list>
/// Pure logic, so the cases can be tested: the hook thread calls <see cref="Handle"/>, which says whether to swallow the key.
/// </summary>
/// <param name="trigger">The key that completes the combination (Space).</param>
/// <param name="modifiersMatch">Whether the other modifiers of the hotkey (Ctrl, Alt, Shift, Win) are held right now.</param>
/// <param name="send">Sends a key to the system again: scan code and whether it goes down (else up).</param>
/// <param name="fire">The hotkey was pressed.</param>
public sealed class HangulCombo(uint trigger, Func<bool> modifiersMatch, Action<uint, bool> send, Action fire, Func<long>? clock = null)
{
    public const uint Hangul = 0x15; // VK_HANGUL, the 한/영 key

    /// <summary>A held-back key whose release never comes (the screen was locked): after this it is let go, never held hostage.</summary>
    const long StaleMs = 3000;

    readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    bool _held, _used, _sentDown;
    uint _scan, _swallowedUp;
    long _at;

    /// <summary>True when the key is to be swallowed.</summary>
    public bool Handle(uint vk, uint scan, bool down)
    {
        long now = _clock();
        if (_held && now - _at > StaleMs) _held = false;

        if (vk == Hangul)
        {
            if (down)
            {
                if (!_held) { _held = true; _used = _sentDown = false; _scan = scan; _swallowedUp = 0; }
                _at = now;
                return true; // the auto-repeat of a held key too
            }
            if (!_held) return false; // went down before we were listening
            _held = false;
            if (_sentDown) send(_scan, false);
            else if (!_used) { send(_scan, true); send(_scan, false); }
            return true;
        }

        if (!down)
        {
            if (vk != _swallowedUp) return false;
            _swallowedUp = 0; // the release of the swallowed trigger
            return true;
        }
        if (!_held) return false;

        _at = now;
        if (vk == trigger && modifiersMatch())
        {
            if (_swallowedUp == vk) return true; // auto-repeat: one press
            _swallowedUp = vk;
            _used = true;
            fire();
            return true;
        }
        if (IsModifier(vk)) return false; // Ctrl+Hangul+Space: the modifier may come after the 한/영 key
        if (!_sentDown) { _sentDown = true; send(_scan, true); }
        _used = true;
        return false;
    }

    /// <summary>Forgets a held key (the hook is going away).</summary>
    public void Reset() { _held = _used = _sentDown = false; _swallowedUp = 0; }

    static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
}
