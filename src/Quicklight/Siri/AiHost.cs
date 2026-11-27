using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Quicklight.Core;
using Quicklight.Core.Ai;
using Quicklight.Mcp;

namespace Quicklight.Siri;

/// <summary>
/// Owns the AI side of the app: the conversation store, the model session, the answer panel and the Siri-style app
/// window. The launcher, the tray, the Explorer menu and the selection overlays all go through <see cref="Ask"/>.
/// </summary>
public sealed class AiHost : IConsent, IDisposable
{
    readonly QuicklightSettings _settings;
    readonly System.Windows.Threading.Dispatcher _ui = Application.Current.Dispatcher;
    readonly KillSwitchTools _control;
    CancellationTokenSource? _cts;
    AnswerWindow? _answer;
    SiriAppWindow? _app;

    public ConversationStore Store { get; }
    public AiSession Session { get; }

    /// <summary>The conversation being answered right now, and what the AI is doing.</summary>
    public Conversation? BusyConversation { get; private set; }
    public AiState State { get; private set; } = AiState.Idle;

    /// <summary>Anything on screen that shows the AI changed: state, messages, history.</summary>
    public event Action? Changed;

    readonly SearchEngine _toolEngine;

    /// <summary>The running host (the settings window stops the model through it before deleting its files).</summary>
    public static AiHost? Current { get; private set; }

    public AiHost(QuicklightSettings settings, Func<IntPtr> messageWindow)
    {
        Current = this;
        _settings = settings;
        Store = new ConversationStore(ConversationStore.DefaultPath);
        Store.Changed += () => _ui.BeginInvoke(() => Changed?.Invoke());

        // The model gets its own engine, like the MCP server: no probing of network paths it might be tricked into asking for.
        var everything = settings.FileSearch ? new Core.Everything.EverythingClient(TimeSpan.FromSeconds(15), failFast: false) : null;
        _toolEngine = new SearchEngine(settings, new UsageStore(null), everything: everything, fileTimeout: TimeSpan.FromSeconds(31), probeRemotePaths: false,
            rates: settings.CurrencyConversion ? SearchEngine.CreateRateStore() : null);
        var tools = new List<IAgentTools>
        {
            new McpToolSource(new McpServer(_toolEngine, everything, new McpServer.Options(AllowOpen: true))),
            new ScreenTools(() => Context),
        };
        // Computer control is its own switch (settings); reading is automatic, every change asks first through this host.
        _control = new KillSwitchTools(new AgentDesktopTools(), messageWindow, () => settings.ComputerControl, Cancel);
        tools.Add(_control);
        Session = new AiSession(Store, tools, this);
        Session.StateChanged += s => _ui.BeginInvoke(() =>
        {
            State = s;
            Changed?.Invoke();
        });
        PurgeOld();
    }

    public bool IsBusy => BusyConversation is not null;

    /// <summary>Settings changed: with Qutra off the model leaves memory at once.</summary>
    public void ApplySettings()
    {
        if (!_settings.AiEnabled) Session.Stop();
        PurgeOld();
    }

    /// <summary>Applies "대화 유지": conversations older than the chosen period go (pinned ones stay).</summary>
    public void PurgeOld()
    {
        TimeSpan? age = _settings.AiKeepConversations switch { "30d" => TimeSpan.FromDays(30), "1y" => TimeSpan.FromDays(365), _ => null };
        if (age is { } a) Store.PurgeOlderThan(a);
    }

    // ---------- asking ----------

    /// <summary>
    /// A question from outside any open panel: starts a new conversation. The status capsule appears where
    /// <paramref name="origin"/> (physical pixels, the launcher's capsule) was, and grows into the answer panel.
    /// </summary>
    public bool Ask(string question, Rect origin, IntPtr returnTo, Brush capsuleTint)
    {
        if (IsBusy) return false;
        if (!_settings.AiEnabled || !AiModel.IsAvailable) { NotAvailable(); return false; }
        var conv = Store.Create();
        _answer ??= new AnswerWindow(this);
        _answer.Begin(conv, question, origin, returnTo, capsuleTint);
        _ = SendAsync(conv, question, []);
        return true;
    }

    /// <summary>Sends one message in <paramref name="conv"/> (a follow-up, or the first message of a new conversation).</summary>
    public async Task SendAsync(Conversation conv, string text, IReadOnlyList<Attachment> attachments)
    {
        if (IsBusy || !_settings.AiEnabled) return; // off means no model in memory, also for a follow-up in an old conversation
        var cts = _cts = new CancellationTokenSource();
        BusyConversation = conv;
        State = AiState.Working;
        Changed?.Invoke();
        try { await Session.AskAsync(conv, text, attachments, cts.Token); }
        catch (OperationCanceledException) { } // Esc, or the panel was closed: the late answer is dropped
        catch (Exception ex) { Log.Error("ai ask failed", ex); }
        finally
        {
            if (_cts == cts) { _cts = null; BusyConversation = null; }
            Changed?.Invoke();
        }
    }

    /// <summary>Stops the answer in progress, including a computer-control action waiting for approval.</summary>
    public void Cancel() => _ui.BeginInvoke(() =>
    {
        _cts?.Cancel();
        _confirm?.TrySetResult(false);
    });

    // ---------- chosen outside the launcher ----------

    /// <summary>Files or text chosen elsewhere: the answer panel opens with them attached, ready for the question.</summary>
    public void Compose(IEnumerable<string> paths, string? draft, IntPtr returnTo)
    {
        if (!_settings.AiEnabled || !AiModel.IsAvailable) { NotAvailable(); return; }
        _answer ??= new AnswerWindow(this);
        _answer.Compose(paths, draft, returnTo);
    }

    // ---------- what the user was looking at ----------

    /// <summary>The window in front, the selected files and text, as of the last time the launcher opened (read by the AI's tools).</summary>
    public LauncherContext? Context { get; private set; }
    int _captureGen;

    /// <summary>
    /// Called as the launcher opens, before it takes the focus. Only reads when the AI is usable; the AI decides later
    /// whether any of it is needed (the get_context tool), nothing is sent anywhere by this.
    /// </summary>
    public void CaptureContext(IntPtr foreground)
    {
        Context = null;
        if (!_settings.AiEnabled || !AiModel.IsAvailable) return;
        int gen = Interlocked.Increment(ref _captureGen);
        Task.Run(() => ScreenTools.Capture(foreground)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && gen == _captureGen) Context = t.Result; // an older, slower capture must not replace a newer one
        });
    }

    static void NotAvailable() =>
        MessageBox.Show("Qutra를 쓰려면 설정에서 모델을 내려받아야 합니다 (RAM 64GB 이상).", "Quicklight", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------- windows ----------

    public void OpenApp(Conversation? conv = null)
    {
        if (_app is null)
        {
            _app = new SiriAppWindow(this);
            _app.Closed += (_, _) => _app = null;
        }
        _app.Open(conv);
    }

    /// <summary>The answer panel's ↗ button: the same conversation continues in the app window.</summary>
    public void Expand(Conversation conv)
    {
        _answer?.CloseQuietly();
        OpenApp(conv);
    }

    public bool AnswerShowing => _answer?.Showing ?? false;

    /// <summary>Closes the answer panel as its own close button does (the app it came from gets the focus back).</summary>
    public void CloseAnswer()
    {
        if (_answer is not { Showing: true }) return;
        _answer.CloseAndReturn();
        ViewClosed();
    }

    public void ShowLast()
    {
        var c = Store.All.FirstOrDefault();
        if (c is null) OpenApp();
        else if (_settings.AiOpenWith == "new") OpenApp();
        else OpenApp(c);
    }

    // ---------- consent ----------

    TaskCompletionSource<bool>? _confirm;
    public string? PendingConfirmation { get; private set; }

    /// <summary>Every action that changes something asks here, in the conversation the user is looking at.</summary>
    public async Task<bool> ConfirmAsync(string summary, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _ui.InvokeAsync(() =>
        {
            if (!HasVisibleView) { tcs.TrySetResult(false); return; } // nobody could answer: refuse instead of hanging
            _confirm = tcs;
            PendingConfirmation = summary;
            Changed?.Invoke();
        });
        using var reg = ct.Register(() => tcs.TrySetResult(false));
        try { return await tcs.Task; }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                if (_confirm == tcs) { _confirm = null; PendingConfirmation = null; Changed?.Invoke(); }
            });
        }
    }

    public void Answer(bool allow) => _confirm?.TrySetResult(allow);

    bool HasVisibleView => (_answer?.Showing ?? false) || (_app?.IsVisible ?? false);

    /// <summary>A window showing the conversation closed: work that nothing on screen shows any more stops.</summary>
    public void ViewClosed()
    {
        if (IsBusy && !HasVisibleView) Cancel();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _control.Dispose();
        Session.Dispose();
        _toolEngine.Dispose();
        Store.Flush();
    }

    // ---------- Esc as a kill switch while the AI is operating other apps ----------

    /// <summary>
    /// Wraps the computer-control tools: nothing is offered unless the setting is on, and while an action that changes
    /// something runs, Esc (a global hotkey) stops everything, even with another app in front.
    /// </summary>
    sealed class KillSwitchTools(IAgentTools inner, Func<IntPtr> window, Func<bool> enabled, Action stop) : IAgentTools, IDisposable
    {
        const int WmHotkey = 0x0312, Id = 0x5151;
        HwndSource? _source;
        int _running;

        public IReadOnlyList<AgentTool> Tools => enabled() ? inner.Tools : [];

        public string Describe(string name, JsonObject args) => inner.Describe(name, args);

        public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
        {
            bool write = inner.Tools.FirstOrDefault(t => t.Name == name) is { ReadOnly: false };
            if (write) Arm(true);
            try { return await inner.CallAsync(name, args, ct); }
            finally { if (write) Arm(false); }
        }

        void Arm(bool on)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var hwnd = window();
                if (hwnd == IntPtr.Zero) return;
                if (on)
                {
                    if (_running++ > 0) return;
                    _source = HwndSource.FromHwnd(hwnd);
                    _source?.AddHook(Hook);
                    RegisterHotKey(hwnd, Id, 0, 0x1B /* VK_ESCAPE */);
                }
                else if (--_running == 0)
                {
                    UnregisterHotKey(hwnd, Id);
                    _source?.RemoveHook(Hook);
                }
            });
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == Id) { handled = true; stop(); }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_running > 0) { _running = 1; Arm(false); }
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
