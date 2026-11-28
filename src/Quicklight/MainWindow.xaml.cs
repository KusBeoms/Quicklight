using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Quicklight.Core;
using Quicklight.Core.Activity;
using Quicklight.Core.Everything;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Core.Update;

namespace Quicklight;

public partial class MainWindow : Window
{
    const string TopHitGroup = ResultItem.HeroGroup;
    const double ShadowMargin = 24;      // DIPs around the panel, room for the drop shadow (matches Shell.Margin)
    double _roomCap = 570;
    const double MaxPanelHeight = 700;   // DIPs; the bar sits so that a fully expanded panel is centered on screen
    const double CardRadius = 36;        // corners of the results card
    const double BarRadiusMax = 48;      // the search capsule is a pill (96 high); a taller (wrapped) one keeps this curve
    const double CardGap = 10;           // DIPs between the capsule and the results card
    const double PillWidth = 720;        // the search pill, and the card except on the app screen
    const double AppCardWidth = 940;     // the card on the app screen: nine tile columns (the window leaves space for it)
    const double ClipCardWidth = 840;    // the card on the clipboard screen
    const double BackdropPad = 48;       // DIPs captured beyond the panel so the blur near its edges has real content
    const double BackdropBlur = 14;      // blur strength (Gaussian sigma) of the backdrop, in DIPs

    readonly SearchEngine _engine;
    readonly QuicklightSettings _settings;
    readonly UpdateService _updates;
    readonly ClipboardHistoryProvider _clipboard;
    IntPtr _previousForeground; // where Paste results go
    readonly IconLoader _icons = new(96); // tiles are 44 DIPs: sharp up to 200% display scaling
    readonly ObservableCollection<ResultItem> _items = [];
    ObservableCollection<ResultItem> _apps = [];
    readonly RecentQueries _recent = new(RecentQueries.DefaultPath);
    readonly ObservableCollection<ResultItem> _parts = []; // the previewed app's parts
    readonly System.Windows.Threading.DispatcherTimer _indexPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    int _unresponsivePolls;
    IReadOnlyList<AppEntry>? _catalogSource;
    CancellationTokenSource? _cts;
    string? _armedKey; // result waiting for the second Enter of a destructive command
    string _lastQuery = "";
    bool _hiding;
    int _frameRate = 60; // refresh rate of the monitor the launcher is on; animations run at this rate

    public IntPtr Handle { get; private set; }

    /// <summary>Waits (briefly) for the recent queries to reach the disk: quitting must not lose or cut off the last one.</summary>
    public void FlushRecent() => _recent.Flush(TimeSpan.FromSeconds(2));

    /// <summary>Keep the window open when it loses focus (--pinned, for screenshots and debugging).</summary>
    public bool Pinned { get; set; }

    public MainWindow(SearchEngine engine, QuicklightSettings settings, UpdateService updates, ClipboardHistoryProvider clipboard)
    {
        _engine = engine;
        _clipboard = clipboard;
        _settings = settings;
        _updates = updates;
        InitializeComponent();
        Theme.ApplyInk(Resources); // the capsule and card are dark glass whatever the system theme, as Siri's are
        TerminalThumb.Attach(TerminalOutput);
        AppGridThumb.Attach(AppGrid);
        ((CollectionViewSource)Resources["GroupedResults"]).Source = _items;
        ((CollectionViewSource)Resources["GroupedApps"]).Source = _apps;
        PreviewParts.ItemsSource = _parts;
        Deactivated += (_, _) => { if (!Pinned && !_handingOffToAi && !_dragging) HideLauncher(); };
        foreach (var list in new[] { Results, AppGrid, PreviewParts }) list.PreviewMouseMove += List_PreviewMouseMove;
        // The window reaches the bottom of the screen so the panel can grow inside it; a click beside the panel closes it.
        MouseDown += (_, e) => { if (!Pinned && e.OriginalSource is Visual v && !BarPanel.IsAncestorOf(v) && !ListPanel.IsAncestorOf(v) && v != ScopeBar && !ScopeBar.IsAncestorOf(v)) HideLauncher(); };
        Query.SelectionChanged += (_, _) => SyncCaret();
        Query.TextChanged += (_, _) => SyncCaret();
        Query.SizeChanged += (_, _) => SyncCaret();
        Query.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => SyncCaret()));
        Query.IsKeyboardFocusedChanged += (_, _) => SyncCaret();
        _indexPoll.Tick += (_, _) => _ = PollIndexAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _indexPoll.Start(); _ = PollIndexAsync(); }
            else _indexPoll.Stop();
        };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement))
            .AddValueChanged(AppGrid, (_, _) => SyncCardWidth());
        Results.LayoutUpdated += (_, _) => MoveGlass(Results);
        AppGrid.LayoutUpdated += (_, _) => MoveGlass(AppGrid);
        PreviewParts.LayoutUpdated += (_, _) => MoveGlass(PreviewParts);
        // Wheel click opens the selected item, wherever the pointer is.
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || Selected is not { } item) return;
            e.Handled = true;
            Execute(item);
        };
        _typeAheadTimer.Tick += (_, _) => ClearTypeAhead();
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            SpringBack();
            OpenPreview();
        };
    }

    bool _gridShown; // the all-apps grid, also while a preview covers it

    // On the all-apps screen typing no longer edits the query: up to ten letters jump to an app instead, until typing pauses.
    bool _gridLocked;
    readonly System.Text.StringBuilder _typeAhead = new();
    readonly System.Windows.Threading.DispatcherTimer _typeAheadTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    const int TypeAheadMax = 10;
    bool IsGrid => _gridShown;
    ListBox ActiveList => IsGrid ? AppGrid : Results;
    ResultItem? Selected => ActiveList.SelectedItem as ResultItem;

    /// <summary>Creates the HWND (needed for the hotkey) without showing the window.</summary>
    public void InitializeHidden()
    {
        Handle = new WindowInteropHelper(this).EnsureHandle();
        MakeTransparent();
        ApplyBackdrop();
    }

    /// <summary>Also done on every show: a display or GPU reset, sleep or a remote session change drops the DWM frame extension and the window turns solid black.</summary>
    void MakeTransparent()
    {
        HwndSource.FromHwnd(Handle).CompositionTarget.BackgroundColor = Colors.Transparent;
        NativeUi.MakeGpuTransparent(Handle);
    }

    /// <summary>"acrylic" = blurred snapshot of what is behind the window; "solid" = opaque panel.</summary>
    public void ApplyBackdrop()
    {
        bool solid = _settings.Backdrop.Equals("solid", StringComparison.OrdinalIgnoreCase);
        Backdrop.Visibility = ListBackdrop.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;
        Tint.SetResourceReference(Border.BackgroundProperty, solid ? "SolidPanelBrush" : "BarTintBrush");
        ListTint.SetResourceReference(Border.BackgroundProperty, solid ? "SolidPanelBrush" : "PanelBrush");
    }

    public void Toggle()
    {
        // IsActive can be stale when a show did not win the foreground, so ask Windows.
        if (IsVisible && !_hiding && NativeUi.IsForeground(Handle)) HideLauncher();
        else ShowLauncher();
    }

    // ---------- show / hide ----------

    public void ShowLauncher()
    {
        // With the answer panel open the hotkey closes it, and the search capsule does not flash up in between.
        if (!IsVisible && Ai is { AnswerShowing: true }) { Ai.CloseAnswer(); return; }
        var (work, scale, hz) = NativeUi.MonitorUnderCursor();
        if (Math.Clamp(hz, 30, 500) != _frameRate) _blink = null;
        _frameRate = Math.Clamp(hz, 30, 500);
        int windowWidthPx = (int)(Width * scale);
        int x = work.Left + (work.Width - windowWidthPx) / 2;
        int panelTop = work.Top + Math.Max(0, (int)((work.Height - MaxPanelHeight * scale) / 2));
        int margin = (int)(ShadowMargin * scale);
        int y = panelTop - margin;
        _roomCap = Math.Max(200, (work.Bottom - panelTop) / scale - 190); // capsule + gaps + footer: small screens must not clip the list
        Results.MaxHeight = Math.Min(_clipMode ? AppGrid.Height : 570, _roomCap);

        if (!IsVisible)
        {
            _previousForeground = NativeUi.Foreground();
            Ai?.CaptureContext(_previousForeground); // before this window takes the focus
            UpdatePlaceholder();
            Status.Text = ""; // a one-off message (e.g. a launch error) must not outlive the opening it was shown in
        }

        // Snapshot the screen under the panel before the window covers it (skipped when still fading out: it would capture itself).
        if (!IsVisible && Backdrop.Visibility == Visibility.Visible)
            CaptureBackdrop(x + margin, panelTop, (int)((Width - 2 * ShadowMargin) * scale), work.Bottom - panelTop, scale);

        // Start invisible: otherwise the panel flashes fully opaque until the animation's first frame.
        Shell.BeginAnimation(OpacityProperty, null);
        Shell.Opacity = 0;
        BarGrid.Opacity = 1;
        _hiding = false; // shown again mid fade-out: removing the fade above means its Completed never fires
        NativeUi.Move(Handle, x, y);
        Height = (work.Bottom - y) / scale;
        MakeTransparent();
        Show();
        Activate();
        if (!NativeUi.ForceForeground(Handle))
        {
            // The very first show can lose the race with layout; retry once the window has rendered.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                if (!IsVisible) return;
                if (!NativeUi.ForceForeground(Handle)) Log.Info("could not take the foreground");
                Activate();
                Keyboard.Focus(Query);
            });
        }
        Query.Focus();
        Keyboard.Focus(Query);
        // Wait for the first frame (layout, backdrop upload, bitmap cache) so the slow start is not eaten by the
        // time-based animation, which would otherwise jump ahead.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (IsVisible && !_hiding) AnimateIn(); });
        _ = WarmUpAfterOpeningAsync();
    }

    /// <summary>
    /// A due app rescan (every ten minutes, on opening) is heavy on the CPU and the GC: start it once the opening
    /// animation is over. A timer lets the rescan happen even if the launcher closes quickly.
    /// </summary>
    async Task WarmUpAfterOpeningAsync()
    {
        await Task.Delay(400);
        await _engine.WarmUpAsync();
    }

    public void HideLauncher(bool aiHandoff = false)
    {
        if (!IsVisible || _hiding) return;
        _voice.Stop(); // dictation must not type into a hidden window
        _updateCts?.Cancel(); // closing the launcher cancels an update check or download
        _cts?.Cancel(); // no point finishing Everything queries nobody will see
        ExitClipActions();
        Disarm();
        CancelHold();
        ClosePreview();
        CloseTerminal();
        if (ActiveList.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        void Done()
        {
            Hide();
            Query.Clear(); // the next opening starts empty (↑ brings back recent queries)
            SetScope(Scope.None); // ... in the plain search, without the chips
            ScopeBar.Visibility = Visibility.Collapsed;
        }
        if (aiHandoff)
        {
            // The answer window takes the place of the search bar; the results underneath can still fade out.
            BarGrid.Opacity = 0;
        }
        if (!UiMotion.Enabled)
        {
            Shell.BeginAnimation(OpacityProperty, null);
            Shell.Opacity = 0;
            Done();
        }
        else AnimateOut(Done, aiHandoff ? UiMotion.Quick : UiMotion.Standard);
    }

    void CaptureBackdrop(int x, int y, int width, int height, double scale)
    {
        int pad = (int)(BackdropPad * scale);
        try
        {
            // One snapshot from the pill down to the screen's bottom; the card shows the part under itself.
            Backdrop.Source = ListBackdrop.Source = ScreenCapture.CaptureBlurred(x - pad, y - pad, width + 2 * pad, height + 2 * pad, scale, BackdropBlur);
            _backdropPad = pad / scale;
            Canvas.SetLeft(Backdrop, -_backdropPad - (ShellWidth - PillWidth) / 2);
            Canvas.SetTop(Backdrop, -_backdropPad);
            PlaceListBackdropTop();
            PlaceListBackdrop();
        }
        catch (Exception ex) { Log.Error("backdrop capture failed", ex); }
    }

    /// <summary>
    /// An animation that ticks at the monitor's refresh rate. WPF animations default to 60 fps, which looks
    /// choppy on 120/144/240 Hz screens.
    /// </summary>
    DoubleAnimation Anim(double? from, double to, double ms, IEasingFunction ease)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
        if (from is { } f) a.From = f;
        Timeline.SetDesiredFrameRate(a, _frameRate);
        return a;
    }

    /// <summary>
    /// While animating, the panel content is drawn once into a GPU bitmap, so each frame only scales, fades and blurs
    /// that bitmap instead of redrawing every row. Dropped afterwards so the settled panel is pixel-sharp.
    /// Nothing is scaled or moved: resampling the cached glyphs (the placeholder, the search icon) at sub-pixel
    /// offsets made them shimmer, whatever the hinting.
    /// </summary>
    void CacheBody(bool on)
    {
        Bar.CacheMode = on ? new BitmapCache { SnapsToDevicePixels = true } : null;
        Body.CacheMode = on ? new BitmapCache { SnapsToDevicePixels = true } : null;
    }

    // Fade in while the content comes into focus (blur → sharp).
    // The blur is on the content only: blurring the panel's edge too made it look like it shrank and grew again.
    void AnimateIn()
    {
        _hiding = false;
        if (!UiMotion.Enabled)
        {
            Body.Effect = Bar.Effect = null;
            CacheBody(false);
            Shell.Opacity = 1;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        const double ms = 200;
        CacheBody(true);
        var blur = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
        Body.Effect = blur;
        Bar.Effect = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
        Shell.BeginAnimation(OpacityProperty, Anim(0, 1, UiMotion.Standard, ease));
        var focus = Anim(8, 0, ms, ease);
        // Drop the effect and the cache once sharp: an idle BlurEffect would still cost a render pass per frame.
        focus.Completed += (_, _) =>
        {
            if (Body.Effect != blur) return; // hiding again already
            Body.Effect = Bar.Effect = null;
            CacheBody(false);
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, focus);
        Bar.Effect.BeginAnimation(BlurEffect.RadiusProperty, Anim(8, 0, ms, ease));
    }

    // Fade out, then hide. No blur: switching the sharp text to the cached, blurred bitmap flickered for a frame.
    void AnimateOut(Action done, int duration)
    {
        _hiding = true;
        var fade = Anim(null, 0, duration, new CubicEase { EasingMode = EasingMode.EaseOut });
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return; // shown again mid-animation
            _hiding = false;
            done();
        };
        Shell.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- caret ----------

    // The native caret is hidden; this bar blinks with a soft fade instead.
    // Layout is forced first so the character rects are current and the caret keeps up with typing. A keystroke raises
    // several of these (text, selection, scroll), and each forced layout is of the whole window: one per keystroke,
    // queued above input and rendering so it still lands before the frame is drawn.
    bool _caretQueued;

    void SyncCaret()
    {
        if (_caretQueued) return;
        _caretQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
        {
            _caretQueued = false;
            Query.UpdateLayout();
            UpdateAskHint();
            UpdateCaret();
        });
    }

    DoubleAnimationUsingKeyFrames? _blink; // made again when the launcher opens on a monitor with another refresh rate

    void UpdateCaret()
    {
        var r = Query.GetRectFromCharacterIndex(Query.CaretIndex);
        Caret.Visibility = CaretGlow.Visibility = Query.IsKeyboardFocused && !r.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (Caret.Visibility != Visibility.Visible) return;
        var p = Query.TranslatePoint(r.TopLeft, CaretLayer);
        Caret.Height = r.Height + 4;
        CaretMove.Y = p.Y - 2;
        CaretMove.X = p.X - 1.5;
        CaretGlow.Height = r.Height + 10;
        CaretGlowMove.Y = p.Y - 5;
        CaretGlowMove.X = p.X - 4;
        if (!UiMotion.Enabled)
        {
            Caret.BeginAnimation(OpacityProperty, null);
            CaretGlow.BeginAnimation(OpacityProperty, null);
            return;
        }
        // Solid while typing or moving, then blink: starting the animation again restarts it from solid.
        if (_blink is null)
        {
            _blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            _blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            _blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
            _blink.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new SineEase()));
            _blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            _blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new SineEase()));
            Timeline.SetDesiredFrameRate(_blink, _frameRate);
            _blink.Freeze();
        }
        Caret.BeginAnimation(OpacityProperty, _blink);
        CaretGlow.BeginAnimation(OpacityProperty, _blink);
    }

    // ---------- shape ----------

    double _backdropPad;
    double ShellWidth => Width - 2 * ShadowMargin;

    /// <summary>The card is centered in the shell; its backdrop stays put on screen as the card widens.</summary>
    void PlaceListBackdrop() => Canvas.SetLeft(ListBackdrop, -_backdropPad - (ShellWidth - ListSurface.ActualWidth) / 2);

    /// <summary>The capsule grows with a wrapped question: its curve, outline and shadow follow, and the card moves down with it.</summary>
    void BarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double r = Math.Min(e.NewSize.Height / 2, BarRadiusMax);
        BarClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        BarClip.RadiusX = BarClip.RadiusY = r;
        BarShape.CornerRadius = BarOutline.CornerRadius = new CornerRadius(r);
        if (e.HeightChanged) PlaceListBackdropTop();
        SyncCaret();
    }

    /// <summary>The card's backdrop stays put on screen: it sits below the capsule, the scope chips (when shown) and the gap.</summary>
    void PlaceListBackdropTop() => Canvas.SetTop(ListBackdrop, -_backdropPad - (BarGrid.ActualHeight + ScopeBar.ActualHeight + CardGap));

    void ScopeBar_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceListBackdropTop();

    readonly Siri.VoiceInput _voice = new();

    /// <summary>The capsule's microphone: what is said is added to the query.</summary>
    async void Mic_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Mic.Opacity = 1;
        Mic.Foreground = (Brush)FindResource("AccentBrush");
        var text = await _voice.ListenAsync(this);
        Mic.Opacity = 0.7;
        Mic.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        if (text is null || !IsVisible) return;
        Query.Text += (Query.Text.Length > 0 ? " " : "") + text;
        Query.CaretIndex = Query.Text.Length;
        Query.Focus();
    }

    bool _askHintWanted;

    /// <summary>
    /// Puts "— AI에게 물어보기" right after the typed question; when the last line has no room it goes on a new line and the
    /// capsule grows by that line.
    /// </summary>
    void UpdateAskHint()
    {
        if (!_askHintWanted || Query.Text.Length == 0)
        {
            AskHint.Visibility = Visibility.Collapsed;
            if (Query.Margin.Bottom != 0) Query.Margin = new Thickness(0);
            return;
        }
        var end = Query.GetRectFromCharacterIndex(Query.Text.Length);
        if (end.IsEmpty) { AskHint.Visibility = Visibility.Collapsed; return; }
        AskHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool fits = end.Right + 12 + AskHint.DesiredSize.Width < Query.ActualWidth;
        double room = fits ? 0 : end.Height;
        if (Math.Abs(Query.Margin.Bottom - room) > 0.5)
        {
            Query.Margin = new Thickness(0, 0, 0, room);
            Query.UpdateLayout();
            end = Query.GetRectFromCharacterIndex(Query.Text.Length);
        }
        var at = Query.TranslatePoint(fits ? new Point(end.Right + 12, end.Top) : new Point(2, end.Bottom), CaretLayer);
        Canvas.SetLeft(AskHint, at.X);
        Canvas.SetTop(AskHint, at.Y);
        AskHint.Visibility = Visibility.Visible;
    }

    void ListSurface_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) PlaceListBackdrop(); }

    /// <summary>On the app screen the card widens (and, through the taller grid, grows) to show more apps.</summary>
    void SyncCardWidth()
    {
        double w = AppGrid.Visibility == Visibility.Visible || _terminalOpen ? AppCardWidth : _clipMode ? ClipCardWidth : PillWidth;
        if (!IsVisible || !UiMotion.Enabled) { ListSurface.BeginAnimation(WidthProperty, null); ListSurface.Width = w; return; }
        ListSurface.BeginAnimation(WidthProperty, Anim(null, w, 260, new CubicEase { EasingMode = EasingMode.EaseOut }));
    }

    void ListPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ListPanel.Clip = new RectangleGeometry(new Rect(e.NewSize), CardRadius, CardRadius);

    /// <summary>
    /// The card glides to the content's new height instead of snapping; set at once while hidden. It fades in with
    /// its content coming into focus (like the launcher itself), and fades out as it shrinks to nothing.
    /// </summary>
    void Body_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged) return;
        double h = e.NewSize.Height;
        if (!IsVisible || !UiMotion.Enabled)
        {
            ListSurface.BeginAnimation(HeightProperty, null);
            ListSurface.BeginAnimation(OpacityProperty, null);
            ListSurface.Height = h;
            ListSurface.Opacity = h > 0 ? 1 : 0;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ListSurface.BeginAnimation(HeightProperty, Anim(null, h, 260, ease));
        ListSurface.BeginAnimation(OpacityProperty, Anim(null, h > 0 ? 1 : 0, h > 0 ? 200 : 180, ease));
        if (e.PreviousSize.Height == 0 && h > 0 && Body.Effect is null) // appearing, and not already blurred by AnimateIn
        {
            var blur = new BlurEffect { Radius = 12, RenderingBias = RenderingBias.Performance };
            Body.Effect = blur;
            var focus = Anim(12, 0, 260, ease);
            focus.Completed += (_, _) => { if (Body.Effect == blur) Body.Effect = null; };
            blur.BeginAnimation(BlurEffect.RadiusProperty, focus);
        }
    }

    // ---------- searching ----------

    void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Disarm();
        ClosePreview();
        CloseTerminal();
        RefreshResults();
    }

    /// <summary>What a search starts from: everything, or one scope picked with Ctrl+1 / 2 / 4 or a chip under the search box.</summary>
    enum Scope { None, Apps, Files, Recent, Clipboard }

    Scope _scope;

    static readonly HashSet<ResultKind> FilesOnly = [ResultKind.File, ResultKind.Folder, ResultKind.Path];

    /// <summary>Puts the results of the current scope and query on screen.</summary>
    void RefreshResults()
    {
        if (_scope == Scope.Apps)
        {
            _cts?.Cancel();
            _lastQuery = "";
            // Laying out every tile takes a moment: let the keystrokes already waiting in first.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (IsVisible && _scope == Scope.Apps) ShowCatalog();
            });
            return;
        }
        if (IsGrid) { AppGrid.Visibility = Visibility.Collapsed; _gridShown = false; }
        _ = RunSearchAsync(Query.Text);
    }

    /// <summary>
    /// Switches the scope. Apps is the all-apps screen (typing jumps to an app, so the box starts empty); Files
    /// searches files and folders only; Clipboard lists the clipboard history, the typed text filtering it.
    /// </summary>
    void SetScope(Scope scope)
    {
        if (_scope == scope) return;
        _scope = scope;
        LockToGrid(scope == Scope.Apps);
        UpdatePlaceholder();
        UpdateScopeBar();
        Disarm();
        ClosePreview();
        CloseTerminal();
        if (scope == Scope.Apps && Query.Text.Length > 0) Query.Clear(); // the change of text brings the screen up
        else RefreshResults();
    }

    void UpdatePlaceholder() => Placeholder.Text = _scope switch
    {
        Scope.Apps => "앱 — 글자를 눌러 이동",
        Scope.Files => "파일과 폴더 검색",
        Scope.Recent => "최근 파일과 폴더 — 입력해서 거르기",
        Scope.Clipboard => "클립보드 기록 — 입력해서 거르기",
        _ => _settings.AiEnabled && Core.Ai.AiModel.IsAvailable ? "묻거나 검색" : "Quicklight 검색",
    };

    /// <summary>The chips under the search box show while the pointer is over it, and as long as a scope is on.</summary>
    void UpdateScopeBar()
    {
        ScopeBar.Visibility = BarGrid.IsMouseOver || ScopeBar.IsMouseOver || _scope != Scope.None ? Visibility.Visible : Visibility.Collapsed;
        ChipApps.IsChecked = _scope == Scope.Apps;
        ChipFiles.IsChecked = _scope == Scope.Files;
        ChipRecent.IsChecked = _scope == Scope.Recent;
        ChipClipboard.IsChecked = _scope == Scope.Clipboard;
    }

    // Moving from the capsule onto a chip leaves one before it enters the other: look once both have settled.
    void ScopeBar_MouseChanged(object sender, MouseEventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => UpdateScopeBar());

    void Chip_Click(object sender, RoutedEventArgs e)
    {
        var scope = Enum.Parse<Scope>((string)((FrameworkElement)sender).Tag);
        SetScope(_scope == scope ? Scope.None : scope);
        UpdateScopeBar(); // a click flips the chip by itself
        Keyboard.Focus(Query);
    }

    async Task RunSearchAsync(string text, bool expandSystemFolders = false, bool expandSystemFiles = false)
    {
        // Safe to dispose right after Cancel: the previous search only ever checks an already-cancelled token.
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        _lastQuery = text.Trim();
        ExitClipActions(); // typing (or any new search) leaves the AI actions for the clipboard list
        if (_lastQuery.Length == 0 && _scope is not (Scope.Clipboard or Scope.Recent))
        {
            Render([], keepSelection: false);
            return;
        }
        try
        {
            if (_scope == Scope.Recent)
            {
                var recent = await _engine.RecentAsync(_lastQuery, token);
                if (!token.IsCancellationRequested) Render(recent, keepSelection: false);
                return;
            }
            if (_scope == Scope.Clipboard)
            {
                var entries = await _clipboard.ListAsync(_lastQuery, token);
                if (!token.IsCancellationRequested) Render(entries, keepSelection: false);
                return;
            }
            // Instant providers first; files slot in as Everything answers (fast name-prefix pass, then the full pass).
            var kinds = _scope == Scope.Files ? FilesOnly : null;
            var fast = await SearchOffUiAsync(text, FileStage.None, expandSystemFolders, expandSystemFiles, kinds, token);
            if (fast is null) return;
            Render(WithAiFileRow(fast), keepSelection: false);
            if (_settings.AiEnabled && Core.Ai.AiModel.IsAvailable && Ai is { } ai && Core.Ai.TranslateParser.Parse(_lastQuery) is { } tr)
            {
                SearchResult Card(string title, string sub, string target) => new()
                {
                    Title = title, Subtitle = sub, Kind = ResultKind.Calculator, Target = target, Action = target.Length > 0 ? ActionType.Copy : ActionType.None, Score = Scores.Calculator,
                };
                var sub = $"{tr.Label}로 번역 · Qutra";
                Render([Card("번역 중…", sub, ""), .. fast], keepSelection: false);
                await Task.Delay(600, token); // translate only after typing pauses
                string? done = null;
                try { done = await ai.Session.TranslateAsync(tr, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Error("translate failed", ex); }
                if (token.IsCancellationRequested) return;
                Render(done is null ? fast : [Card(done, sub, done), .. fast], keepSelection: false);
                return; // no file passes for a translation
            }
            // Later passes bring files, and network paths (too slow to check on every keystroke); skip them when there are neither.
            if ((_lastQuery.Length < 2 || !_settings.FileSearch) && !PathProvider.IsNetworkQuery(_lastQuery)) return;

            // Wait for a pause in typing: every Everything query takes its whole index (about a second on a big one),
            // and one per keystroke kept it so busy that Windows took it for hung.
            await Task.Delay(200, token);
            IReadOnlyList<SearchResult> results = fast;
            foreach (var stage in new[] { FileStage.Prefix, FileStage.Full })
            {
                if (await SearchOffUiAsync(text, stage, expandSystemFolders, expandSystemFiles, kinds, token) is not { } staged) return;
                Render(WithAiFileRow(results = staged), keepSelection: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("search failed", ex); }
    }

    /// <summary>
    /// Runs the search on the thread pool, so matching and ranking never hold up typing, then comes back only once the
    /// keystrokes already waiting have been handled: the card is drawn after the search box has caught up. Null when a
    /// newer query took over meanwhile.
    /// </summary>
    async Task<IReadOnlyList<SearchResult>?> SearchOffUiAsync(string text, FileStage files, bool expandSystemFolders, bool expandSystemFiles,
        ISet<ResultKind>? kinds, CancellationToken token)
    {
        var results = await Task.Run(() => _engine.SearchAsync(text, ct: token, files: files, kinds: kinds,
            expandSystemFolders: expandSystemFolders, expandSystemFiles: expandSystemFiles), token);
        if (token.IsCancellationRequested) return null;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        return token.IsCancellationRequested ? null : results;
    }

    void Render(IReadOnlyList<SearchResult> results, bool keepSelection)
    {
        // Later file stages keep whatever is selected, including the top hit, so Enter opens what the user saw.
        string? selectedKey = keepSelection && Results.SelectedItem is ResultItem sel ? sel.Result.Key : null;

        // Top hit first, then one section per kind in order of each kind's best result.
        var ordered = new List<ResultItem>();
        if (results.Count > 0) ordered.Add(NewItem(results[0], TopHitGroup));
        foreach (var group in results.Skip(1).GroupBy(r => ResultItem.KindLabel(r.Kind)))
            ordered.AddRange(group.Select(r => NewItem(r, group.Key)));

        // A later file stage often brings the very same rows: keep them (and their containers, icons and a Space hold
        // on one of them) instead of building them all again.
        if (ordered.Count == 0 || !SameRows(ordered))
        {
            _items.Clear();
            int generation = _icons.NextGeneration();
            foreach (var item in ordered)
            {
                _items.Add(item);
                if (item.Result.Key == _armedKey) item.SetSubtitleOverride(ArmedText);
                if (item.Result.Action == ActionType.Update && _updateStatus is not null) item.SetSubtitleOverride(_updateStatus);
                LoadIcon(item, generation);
            }
        }

        // The armed command dropped out of the list: forget it, so it can never run on a single Enter later.
        if (_armedKey is not null && !ordered.Exists(i => i.Result.Key == _armedKey)) _armedKey = null;

        if (ordered.Exists(i => i.Result.Action == ActionType.Update)) _ = CheckForUpdateAsync();
        if (ordered.Exists(i => i.Result.Action == ActionType.AskAi)) AskAiWarmUp(); // a question is coming: load the model now
        _askHintWanted = ordered.Count > 0 && ordered[0].Result.Action == ActionType.AskAi;
        SyncCaret();

        bool any = _items.Count > 0;
        SetClipMode(any && ordered.TrueForAll(i => i.Result.Kind == ResultKind.Clipboard));
        Results.Visibility = Footer.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (_previewOpen || _terminalOpen) Results.Visibility = Visibility.Collapsed; // a later file stage while the preview or terminal is open
        if (!any) return;
        int index = selectedKey is null ? 0 : Math.Max(0, ordered.FindIndex(i => i.Result.Key == selectedKey));
        Select(index, disarm: false);
        // The rows were only just added, so that scroll ran before they were laid out: once they are, bring the
        // selection into view (the list may be taller than the card, and a kept selection may be far down).
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (Results.IsVisible && Results.SelectedIndex >= 0) SelectIn(Results, Results.SelectedIndex, disarm: false);
        });
    }

    /// <summary>The rows on screen show exactly these: same results, same order, same text.</summary>
    bool SameRows(List<ResultItem> rows)
    {
        if (rows.Count != _items.Count) return false;
        for (int i = 0; i < rows.Count; i++)
        {
            ResultItem a = rows[i], b = _items[i];
            if (a.Group != b.Group || a.Result.Key != b.Result.Key || a.Title != b.Title || a.Result.Subtitle != b.Result.Subtitle
                || a.Result.Action != b.Result.Action || a.Body != b.Body
                || a.Result.IconSource != b.Result.IconSource || a.Result.App != b.Result.App) return false; // a rescan's new app entry
        }
        return true;
    }

    ResultItem NewItem(SearchResult r, string group) => new(r, group)
    {
        Body = r.Kind == ResultKind.Clipboard && _clipboard.Text(r.Target) is { } text
            ? (text.Length > 600 ? text[..600] : text).ReplaceLineEndings("\n").Trim('\n')
            : null,
    };

    bool _clipMode;

    /// <summary>The clipboard history gets the app screen's room: the wide card and a taller list of large cards.</summary>
    void SetClipMode(bool on)
    {
        if (_clipMode == on) return;
        _clipMode = on;
        Results.MaxHeight = Math.Min(on ? AppGrid.Height : 500, _roomCap);
        Results.Height = on ? AppGrid.Height : double.NaN;
        PreviewPanel.Height = on ? AppGrid.Height - 20 : 440;
        SyncCardWidth();
    }

    void LoadIcon(ResultItem item, int generation)
    {
        if (item.Result.Kind == ResultKind.Clipboard && _clipboard.Thumbnail(item.Result.Target) is { } thumb) { item.Icon = thumb; return; }
        if (item.Icon is not null || item.Result.IconSource is not { } src) return;
        bool isFolder = item.Result.Kind == ResultKind.Folder || item.Result.Kind == ResultKind.Path && src.EndsWith('\\');
        _icons.Load(src, isFolder, generation, img =>
        {
            if (Dispatcher.CheckAccess()) item.Icon = img;
            else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => item.Icon = img); // after typing
        });
    }

    // ---------- all apps grid ----------

    void ShowCatalog()
    {
        var source = _engine.Apps.All;
        if (!ReferenceEquals(source, _catalogSource))
        {
            // Build once per app index; the tiles keep their icons between openings.
            _catalogSource = source;
            _apps = new(AppCatalog.Build(source).SelectMany(s => s.Apps.Select(app => new ResultItem(AppProvider.ToResult(app), s.Name))));
            ((CollectionViewSource)Resources["GroupedApps"]).Source = _apps; // one reset instead of a regrouping per tile
        }
        int generation = _icons.NextGeneration();
        foreach (var item in _apps) LoadIcon(item, generation);

        _items.Clear();
        Results.Visibility = Visibility.Collapsed;
        AppGrid.Visibility = Footer.Visibility = Visibility.Visible;
        _gridShown = true;
        if (_apps.Count == 0) { FooterText.Text = "앱 목록을 읽는 중입니다…"; return; }
        AppGrid.SelectedIndex = 0;
        AppGrid.ScrollIntoView(AppGrid.SelectedItem);
        UpdateFooter();
        if (_typeAhead.Length > 0) TypeAheadChanged(); // letters typed before the grid was up
    }

    /// <summary>
    /// The all-apps screen takes the keyboard: the query is read-only (Backspace gives it back) and letters go to the
    /// jump buffer. The IME is off meanwhile, so keys arrive as Latin letters; Korean names are matched through the
    /// keyboard-layout conversion ("zkzk" → 카카).
    /// </summary>
    void LockToGrid(bool on)
    {
        if (_gridLocked == on) return;
        _gridLocked = on;
        Query.IsReadOnly = on;
        InputMethod.SetIsInputMethodEnabled(Query, !on);
        if (!on) ClearTypeAhead();
    }

    void Query_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_gridLocked) return;
        e.Handled = true;
        foreach (var c in e.Text)
            if (!char.IsControl(c) && !char.IsWhiteSpace(c) && _typeAhead.Length < TypeAheadMax) _typeAhead.Append(c);
        TypeAheadChanged();
    }

    void ClearTypeAhead()
    {
        _typeAheadTimer.Stop();
        _typeAhead.Clear();
        TypeAhead.Text = "";
        TypeAhead.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the buffer at the right of the search box and selects the app it most likely means.</summary>
    void TypeAheadChanged()
    {
        _typeAheadTimer.Stop();
        if (_typeAhead.Length == 0) { ClearTypeAhead(); return; }
        TypeAhead.Text = _typeAhead.ToString();
        TypeAhead.Visibility = Visibility.Visible;
        _typeAheadTimer.Start();
        if (IsGrid && BestTile(_typeAhead.ToString()) is var i and >= 0) SelectIn(AppGrid, i);
    }

    /// <summary>
    /// One letter: the first app of its section ("c" → C, or the ㅊ section when there is no C). More: the best match
    /// by name, also read as Korean keys ("zkzkdh" → 카카오), the earlier app on a tie.
    /// </summary>
    int BestTile(string typed)
    {
        var hangul = Hangul.QwertyToHangul(typed);
        if (typed.Length == 1)
        {
            foreach (var section in new[] { AppCatalog.SectionOf(typed), hangul is null ? null : AppCatalog.SectionOf(hangul) })
                if (section is not null)
                    for (int i = 0; i < _apps.Count; i++)
                        if (_apps[i].Group == section) return i;
            return -1;
        }
        double best = 0;
        int at = -1;
        for (int i = 0; i < _apps.Count; i++)
        {
            var r = _apps[i].Result;
            double m = Math.Max(FuzzyMatcher.Score(typed, r.Title), hangul is null ? 0 : FuzzyMatcher.Score(hangul, r.Title));
            foreach (var alias in r.App?.Aliases ?? []) m = Math.Max(m, FuzzyMatcher.Score(typed, alias));
            if (m > best) (best, at) = (m, i);
        }
        return best >= 50 ? at : -1;
    }

    /// <summary>Up/Down in the grid: the nearest tile in the next row above or below, across section breaks.</summary>
    void MoveInGrid(int direction)
    {
        if (AppGrid.ItemContainerGenerator.ContainerFromIndex(AppGrid.SelectedIndex) is not ListBoxItem current) return;
        var here = current.TranslatePoint(new Point(0, 0), AppGrid);
        var candidates = new List<(int Index, Point At)>();
        for (int i = 0; i < _apps.Count; i++)
            if (AppGrid.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c)
                candidates.Add((i, c.TranslatePoint(new Point(0, 0), AppGrid)));

        var row = candidates.Where(c => direction > 0 ? c.At.Y > here.Y + 10 : c.At.Y < here.Y - 10).ToList();
        if (row.Count == 0) return;
        double rowY = direction > 0 ? row.Min(c => c.At.Y) : row.Max(c => c.At.Y);
        var target = row.Where(c => Math.Abs(c.At.Y - rowY) < 10).MinBy(c => Math.Abs(c.At.X - here.X));
        SelectIn(AppGrid, target.Index);
    }

    // ---------- keyboard and mouse ----------

    void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        // Another key while Space is still down: the Space was a tap, so it goes in first to keep the typing order.
        if (_holding && e.Key != Key.Space) FinishHold();
        // Ctrl+, : Quicklight settings. Ctrl+Shift+, : Everything's own settings (indexed folders and drives, exclusions).
        if (e.Key == Key.OemComma && mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Alt))
        {
            if (mods.HasFlag(ModifierKeys.Shift)) _ = OpenEverythingOptionsAsync();
            else
            {
                HideLauncher();
                ((App)Application.Current).OpenSettings();
            }
            e.Handled = true;
            return;
        }
        // Ctrl+1 apps, Ctrl+2 files, Ctrl+3 recent, Ctrl+4 clipboard: the scope, or back to the plain search when it is already on.
        if (mods == ModifierKeys.Control && e.Key switch
            {
                Key.D1 or Key.NumPad1 => Scope.Apps,
                Key.D2 or Key.NumPad2 => Scope.Files,
                Key.D3 or Key.NumPad3 => Scope.Recent,
                Key.D4 or Key.NumPad4 => Scope.Clipboard,
                _ => (Scope?)null,
            } is { } wanted)
        {
            SetScope(_scope == wanted ? Scope.None : wanted);
            e.Handled = true;
            return;
        }
        // Menu key or Shift+F10 toggles the selected item's context menu on release (opening on press let the release close it).
        if (IsMenuKey(e)) { e.Handled = true; return; }
        if (_previewOpen)
        {
            if (e.Key == Key.Space && e.IsRepeat) { e.Handled = true; return; } // still holding the Space that opened it
            if (e.Key is Key.Escape or Key.Space) { ClosePreview(); e.Handled = true; return; }
            if (e.Key == Key.Tab && mods == ModifierKeys.None && SummarizablePath() is { } sumPath) // a file: Qutra's summary, Tab again for the file
            {
                if (_summaryMode) OpenPreview(); else _ = ShowSummaryAsync(sumPath);
                e.Handled = true;
                return;
            }
            if (_parts.Count > 0) // an app: ↑↓ pick one of its parts, Enter runs it
            {
                if (e.Key is Key.Up or Key.Down) { SelectIn(PreviewParts, PreviewParts.SelectedIndex + (e.Key == Key.Down ? 1 : -1)); e.Handled = true; return; }
                if (e.Key == Key.Enter && mods == ModifierKeys.None && PreviewParts.SelectedItem is ResultItem part) { Execute(part); e.Handled = true; return; }
            }
            else if (e.Key is Key.Up or Key.Down && !IsGrid)
            {
                Select(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1));
                if (Selected is { } next && CanPreview(next.Result)) OpenPreview();
                else ClosePreview();
                e.Handled = true;
                return;
            }
        }
        // Holding Space on a file, folder or app opens its preview; a tap still types a space.
        if (e.Key == Key.Space && mods == ModifierKeys.None && Selected is { } held && CanPreview(held.Result))
        {
            if (!e.IsRepeat && !_holding) StartHold(held);
            e.Handled = true;
            return;
        }
        if (_gridLocked && e.Key == Key.Back)
        {
            if (_typeAhead.Length > 0) { _typeAhead.Length--; TypeAheadChanged(); e.Handled = true; return; }
            SetScope(Scope.None); // an empty buffer: Backspace leaves the all-apps screen
            e.Handled = true;
            return;
        }
        // Like a terminal: ↑ in an empty box brings back the last query, again for older ones; ↓ goes back toward empty.
        if (e.Key is Key.Up or Key.Down && mods == ModifierKeys.None && BrowsingRecent && (e.Key == Key.Up || Query.Text.Length > 0))
        {
            RecallQuery(e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && mods == ModifierKeys.None && ToggleClipActions()) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Down when IsGrid: MoveInGrid(+1); e.Handled = true; break;
            case Key.Up when IsGrid: MoveInGrid(-1); e.Handled = true; break;
            case Key.Right when IsGrid: SelectIn(AppGrid, AppGrid.SelectedIndex + 1); e.Handled = true; break;
            case Key.Left when IsGrid: SelectIn(AppGrid, AppGrid.SelectedIndex - 1); e.Handled = true; break;
            case Key.Down: Select(Results.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: Select(Results.SelectedIndex - 1); e.Handled = true; break;
            case Key.PageDown: SelectIn(ActiveList, ActiveList.SelectedIndex + (IsGrid ? 27 : 5)); e.Handled = true; break; // three rows of nine
            case Key.PageUp: SelectIn(ActiveList, ActiveList.SelectedIndex - (IsGrid ? 27 : 5)); e.Handled = true; break;
            case Key.Enter when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                if (Selected is { } toElevate && ShellLauncher.CanRunAsAdmin(toElevate.Result.RevealPath ?? toElevate.Result.Target))
                    RunAsAdmin(toElevate, toElevate.Result.RevealPath ?? toElevate.Result.Target);
                e.Handled = true; break;
            case Key.System when e.SystemKey == Key.Enter: // Alt+Enter
                if (Selected is { } toInspect) ShowProperties(toInspect);
                e.Handled = true; break;
            case Key.Delete when Selected is { Result: { Action: ActionType.Timer, Target: var timerTarget } } && timerTarget.StartsWith("cancel|"):
                Quicklight.Core.Providers.TimerService.Cancel(timerTarget[7..]);
                _ = RunSearchAsync(Query.Text);
                e.Handled = true; break;
            case Key.Delete when Selected is { Result.Kind: ResultKind.Clipboard } entry:
                if (_clipboard.Delete(entry.Result.Target)) _ = RunSearchAsync(Query.Text);
                e.Handled = true; break;
            case Key.Enter when mods.HasFlag(ModifierKeys.Control):
                if (Selected is { } toReveal) Reveal(toReveal);
                e.Handled = true; break;
            case Key.Enter:
                if (Selected is { } item) Execute(item);
                e.Handled = true; break;
            case Key.Escape:
                if (_terminalOpen) HideLauncher(); // the first Esc (in the terminal input) already moved here; this one closes
                else if (_updateBusy && _updateCts is { IsCancellationRequested: false } updating) updating.Cancel(); // "Esc로 취소"
                else if (_armedKey is not null) Disarm();
                else if (_clipAiId is not null) { ExitClipActions(); _ = RunSearchAsync(Query.Text); }
                else if (_typeAhead.Length > 0) ClearTypeAhead();
                else if (Query.Text.Length > 0) Query.Clear();
                else if (_scope != Scope.None) SetScope(Scope.None);
                else HideLauncher();
                e.Handled = true; break;
            case Key.C when mods == ModifierKeys.Control && _clipAiResult && Query.SelectionLength == 0 && Selected is { } result:
                if (TrySetClipboard(result.Result.Target)) Status.Text = "복사했습니다";
                e.Handled = true; break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                if (Selected is { } toCopy) CopyPath(toCopy);
                e.Handled = true; break;
        }
    }

    /// <summary>
    /// ↑↓ go through the recent queries while the box is empty or its whole text is selected: a recalled query comes in
    /// selected, and Ctrl+A or dragging over all of it goes back to browsing. Any caret or partial selection moves among the results.
    /// </summary>
    bool BrowsingRecent => Query.Text.Length == 0 || Query.SelectionLength == Query.Text.Length;

    /// <summary>Puts the next older (+1) or newer (-1) recent query in the box, selected; past the newest the box is empty again.</summary>
    void RecallQuery(int step)
    {
        int at = Query.Text.Length == 0 ? -1 : IndexOfRecent(Query.Text);
        if (at < 0 && Query.Text.Length > 0 && step < 0) return; // not a recent query: nothing newer than it
        int i = at + step;
        if (i >= _recent.Items.Count) return; // the oldest is showing
        Query.Text = i < 0 ? "" : _recent.Items[i];
        Query.SelectAll();
    }

    int IndexOfRecent(string text)
    {
        for (int i = 0; i < _recent.Items.Count; i++)
            if (_recent.Items[i] == text.Trim()) return i;
        return -1;
    }

    bool _menuKeyDown; // the release only counts if the press was Menu or Shift+F10 (Shift may be let go first)

    bool IsMenuKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.Apps or Key.F10)) return false;
        if (e.IsDown) return _menuKeyDown = key == Key.Apps ? Keyboard.Modifiers == ModifierKeys.None : Keyboard.Modifiers == ModifierKeys.Shift;
        var was = _menuKeyDown;
        _menuKeyDown = false;
        return was;
    }

    void Query_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsMenuKey(e))
        {
            e.Handled = true;
            if (ActiveList.SelectedItem is ResultItem sel && ActiveList.ItemContainerGenerator.ContainerFromItem(sel) is ListBoxItem row)
                ShowMenu(ActiveList, sel, row, System.Windows.Controls.Primitives.PlacementMode.Bottom);
            return;
        }
        if (e.Key != Key.Space || !_holding) return;
        e.Handled = true;
        FinishHold();
    }

    /// <summary>Ends a Space hold: a tap (preview not opened yet) types the space.</summary>
    void FinishHold()
    {
        bool tapped = _holdTimer.IsEnabled;
        CancelHold();
        if (!tapped || _gridLocked) return; // the preview opened, or the grid has the keyboard
        int start = Query.SelectionStart;
        Query.SelectedText = " ";
        Query.Select(start + 1, 0);
    }

    static (ListBoxItem? Container, ResultItem? Item) HitItem(ListBox list, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) as ListBoxItem;
        return (container, container?.DataContext as ResultItem);
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep keyboard focus in the search box; clicking selects, double-clicking opens.
        var list = (ListBox)sender;
        _dragItem = HitItem(list, e).Item;
        _dragStart = e.GetPosition(null);
        if (_dragItem is not { } item) return;
        e.Handled = true;
        list.SelectedItem = item;
        UpdateFooter();
        if (e.ClickCount >= 2) Execute(item);
        Query.Focus();
    }

    // Drag a file, folder, app or clipboard text row out of the launcher into another window.
    ResultItem? _dragItem;
    Point _dragStart;
    bool _dragging;

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragItem = null; return; }
        if (_dragItem is null || _dragging) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        var data = DragDataFor(item);
        if (data is null) return;
        _dragging = true;
        Opacity = 0.4; // the drop target stays visible under the launcher
        DragDropEffects effect;
        try { effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Link); }
        finally { Opacity = 1; _dragging = false; }
        if (effect != DragDropEffects.None) HideLauncher();
        else { Activate(); Query.Focus(); } // a cancelled drag left another window active; take focus back so clicking away still closes
    }

    DataObject? DragDataFor(ResultItem item)
    {
        var r = item.Result;
        if (r.Kind == ResultKind.Clipboard)
            return _clipboard.Text(r.Target) is { } text ? new DataObject(DataFormats.UnicodeText, text) : null;
        string? path = r.Kind is ResultKind.File or ResultKind.Folder or ResultKind.Path ? r.Target : r.App?.FilePath;
        if (path is null || !(System.IO.File.Exists(path) || System.IO.Directory.Exists(path))) return null;
        return new DataObject(DataFormats.FileDrop, new[] { path });
    }

    int _wheel;

    /// <summary>The wheel moves the selection in the results, one item per notch, instead of scrolling. The app grid scrolls.</summary>
    void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => WheelSelect(Results, e);

    void WheelSelect(ListBox list, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _wheel += e.Delta; // precision touchpads send fractions of a notch
        for (; Math.Abs(_wheel) >= Mouse.MouseWheelDeltaForOneLine; _wheel -= Math.Sign(_wheel) * Mouse.MouseWheelDeltaForOneLine)
            SelectIn(list, list.SelectedIndex + (_wheel > 0 ? -1 : 1));
    }

    /// <summary>Over the preview the wheel moves the selection among an app's parts, like ↑↓; a file's or folder's text scrolls.</summary>
    void PreviewPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_parts.Count > 0) { WheelSelect(PreviewParts, e); return; }
        if (!PreviewText.IsVisible) return;
        PreviewText.ScrollToVerticalOffset(PreviewText.VerticalOffset - e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * 48); // three 16 px lines per notch
        e.Handled = true;
    }

    void List_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var (container, item) = HitItem(list, e);
        if (container is null || item is null) return;
        e.Handled = true;
        list.SelectedItem = item;
        UpdateFooter();
        ShowMenu(list, item, container, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
    }

    void ShowMenu(ListBox list, ResultItem item, FrameworkElement container, System.Windows.Controls.Primitives.PlacementMode placement)
    {
        var menu = BuildMenu(item);
        menu.PlacementTarget = container;
        menu.Placement = placement;
        list.ContextMenu = menu;
        void focusQuery(object? s, RoutedEventArgs a) => Query.Focus();
        menu.Closed += focusQuery;
        // Menu key / Shift+F10 again closes it; Up/Down move through the items, starting from the first.
        // Right-clicking a row while the menu is open moves it there in one step; the default would close it, then the release would reopen it (a blink).
        menu.AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Right || e.OriginalSource is not DependencyObject src
                || ItemsControl.ContainerFromElement(list, src) is not ListBoxItem row || row.DataContext is not ResultItem next) return;
            e.Handled = true;
            menu.Closed -= focusQuery;
            menu.IsOpen = false;
            list.SelectedItem = next;
            UpdateFooter();
            ShowMenu(list, next, row, placement);
        }));
        menu.PreviewKeyDown += (_, e) => { if (IsMenuKey(e)) e.Handled = true; };
        menu.PreviewKeyUp += (_, e) => { if (IsMenuKey(e)) { menu.IsOpen = false; e.Handled = true; } };
        if (placement == System.Windows.Controls.Primitives.PlacementMode.Bottom)
            menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(() => (menu.ItemContainerGenerator.ContainerFromIndex(0) as MenuItem)?.Focus(),
                System.Windows.Threading.DispatcherPriority.Input);
        menu.IsOpen = true;
    }

    ContextMenu BuildMenu(ResultItem item)
    {
        var r = item.Result;
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)) };
        void Add(string glyph, string header, Action action)
        {
            var mi = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 },
                Style = (Style)FindResource(typeof(MenuItem)),
            };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Add("", r.Action == ActionType.Copy ? "복사" : "열기", () => Execute(item));
        if (ShellLauncher.CanRunAsAdmin(r.RevealPath ?? r.Target))
            Add("", "관리자 권한으로 실행", () => RunAsAdmin(item, r.RevealPath ?? r.Target));
        if (r.RevealPath is not null)
        {
            Add("", "파일 탐색기에서 열기", () => Reveal(item));
            Add("", "경로 복사", () => CopyPath(item));
            Add("", "속성", () => ShowProperties(item));
        }
        if (r.App?.Part(AppPartKind.Installer) is { } installer) Add("", "설치 파일 실행", () => RunDirectly(AppProvider.PartResult(r.App, installer)));
        if (r.App?.Part(AppPartKind.Uninstaller) is { } uninstaller) Add("", "제거", () => RunDirectly(AppProvider.PartResult(r.App, uninstaller)));
        return menu;
    }

    void Select(int index, bool disarm = true) => SelectIn(Results, index, disarm);

    void SelectIn(ListBox list, int index, bool disarm = true)
    {
        if (list.Items.Count == 0) return;
        index = Math.Clamp(index, 0, list.Items.Count - 1);
        if (disarm && list.SelectedIndex != index) Disarm();
        list.SelectedIndex = index;
        // The first item of a section brings its section header into view too.
        bool firstInGroup = index == 0 || ((ResultItem)list.Items[index - 1]).Group != ((ResultItem)list.Items[index]).Group;
        if (firstInGroup && list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem c && c.IsArrangeValid)
            c.BringIntoView(new Rect(0, -GroupHeaderHeight, c.ActualWidth, c.ActualHeight + GroupHeaderHeight));
        else list.ScrollIntoView(list.SelectedItem);
        UpdateFooter();
    }

    const double GroupHeaderHeight = 30; // GroupHeader (margin and text) plus the list's top padding

    readonly Dictionary<ListBox, Rect> _glassAt = [];

    sealed record GlassParts(Grid Glass, Grid Host, Border Lens, Grid Light);
    readonly Dictionary<ListBox, GlassParts> _glassParts = [];

    /// <summary>The glass pane's pieces in the list's template (a static resource, so applied once), looked up once.</summary>
    GlassParts? PartsOf(ListBox list)
    {
        if (_glassParts.TryGetValue(list, out var parts)) return parts;
        if (list.Template.FindName("Glass", list) is not Grid glass || list.Template.FindName("GlassHost", list) is not Grid host
            || list.Template.FindName("GlassLens", list) is not Border lens || list.Template.FindName("GlassLight", list) is not Grid light) return null;
        return _glassParts[list] = new GlassParts(glass, host, lens, light);
    }

    /// <summary>Glides the list's glass pane onto the selected item.</summary>
    void MoveGlass(ListBox list)
    {
        // LayoutUpdated comes for every layout pass anywhere in the window (every frame while the card animates), to all
        // three lists: a hidden one only makes sure its glass is hidden, so it does not slide in from an old spot later.
        if (!list.IsVisible)
        {
            if (_glassAt.Remove(list) && _glassParts.TryGetValue(list, out var hidden)) hidden.Glass.Visibility = Visibility.Hidden;
            return;
        }
        if (PartsOf(list) is not { } parts) return;
        var (glass, host) = (parts.Glass, parts.Host);
        if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is not ListBoxItem c
            || VisualTreeHelper.GetParent(c) is not Visual parent || !host.IsAncestorOf(c))
        {
            glass.Visibility = Visibility.Hidden;
            _glassAt.Remove(list);
            return;
        }
        UpdateLens(parts);
        // Layout position only: the Space-hold swell (a render transform on the row) is shared with the glass instead.
        var at = new Rect(parent.TransformToVisual(host).Transform((Point)VisualTreeHelper.GetOffset(c)), c.RenderSize);
        if (_glassAt.TryGetValue(list, out var was) && was == at) return;
        _glassAt[list] = at;

        bool slide = glass.Visibility == Visibility.Visible && UiMotion.Enabled;
        glass.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut }; // about CSS "transition: all .3s ease"
        void To(DependencyProperty p, double v)
        {
            if (slide) glass.BeginAnimation(p, Anim(null, v, 300, ease));
            else { glass.BeginAnimation(p, null); glass.SetValue(p, v); }
        }
        To(Canvas.LeftProperty, at.X);
        To(Canvas.TopProperty, at.Y);
        To(WidthProperty, at.Width);
        To(HeightProperty, at.Height);
    }

    const double LensZoom = 1.12;

    /// <summary>
    /// Shows the blurred backdrop behind the glass, magnified about its center, as if seen through a lens. Runs on
    /// every layout pass, so it follows the glass while it slides and the list while it scrolls.
    /// </summary>
    void UpdateLens(GlassParts parts)
    {
        var (glass, lens, light) = (parts.Glass, parts.Lens, parts.Light);
        // The inner light is blurred; keep it inside the rounded pane.
        var size = glass.RenderSize;
        if (light.Clip is not RectangleGeometry clip || clip.Rect.Size != size)
            light.Clip = new RectangleGeometry(new Rect(size), 12, 12);

        if (ListBackdrop.Source is null || !ListBackdrop.IsVisible || size.Width == 0) { lens.Background = null; return; }
        var seen = glass.TransformToVisual(ListBackdrop).TransformBounds(new Rect(size));
        Rect viewbox;
        if ((lens.Effect ??= GlassRefraction.TryCreate()) is GlassRefraction refraction)
        {
            // The shader magnifies and bends; it gets the backdrop behind the pane plus a margin to bend in.
            lens.CornerRadius = new CornerRadius(0); // the shader cuts the rounded shape itself
            refraction.Width = size.Width;
            refraction.Height = size.Height;
            viewbox = Rect.Inflate(seen, GlassRefraction.Margin * seen.Width / size.Width, GlassRefraction.Margin * seen.Height / size.Height);
        }
        else
        {
            double w = seen.Width / LensZoom, h = seen.Height / LensZoom;
            viewbox = new Rect(seen.X + (seen.Width - w) / 2, seen.Y + (seen.Height - h) / 2, w, h);
        }
        if (lens.Background is not VisualBrush brush)
            lens.Background = brush = new VisualBrush(ListBackdrop) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        if (brush.Viewbox != viewbox) brush.Viewbox = viewbox;
    }

    // ---------- clipboard text: AI actions (Tab) ----------

    // The entry whose actions are showing (null while the clipboard list does), and whether the result card is up.
    string? _clipAiId;
    bool _clipAiResult;
    CancellationTokenSource? _clipAiCts;

    bool ClipActionsAvailable => _settings.AiEnabled && Core.Ai.AiModel.IsAvailable && Ai is not null;

    /// <summary>Tab on a clipboard text entry shows its actions in place of the list; Tab again (or Esc, or typing) brings the list back.</summary>
    bool ToggleClipActions()
    {
        if (_clipAiId is not null) { ExitClipActions(); _ = RunSearchAsync(Query.Text); return true; }
        if (_scope != Scope.Clipboard || _previewOpen || !ClipActionsAvailable) return false;
        if (Selected is not { Result: { Kind: ResultKind.Clipboard, Action: ActionType.Paste } entry } || _clipboard.Text(entry.Target) is null) return false;
        _clipAiId = entry.Target;
        _clipAiResult = false;
        Render(Core.Ai.ClipboardActions.All.Select(a => new SearchResult
        {
            Title = a.Label, Subtitle = "Qutra", Kind = ResultKind.Snippet, Target = a.Id, Action = ActionType.Open, Score = Scores.Clipboard,
        }).ToList(), keepSelection: false);
        return true;
    }

    void ExitClipActions()
    {
        _clipAiId = null;
        _clipAiResult = false;
        _clipAiCts?.Cancel();
    }

    /// <summary>Runs one action on the entry: JSON that parses is done here, the rest by Qutra; the answer is a card that Enter pastes.</summary>
    async Task RunClipActionAsync(string actionId)
    {
        if (_clipAiId is not { } id || Core.Ai.ClipboardActions.Find(actionId) is not { } action || _clipboard.Text(id) is not { } source) return;
        _clipAiCts?.Cancel();
        var cts = _clipAiCts = new CancellationTokenSource();
        var (input, truncated) = Core.Ai.ClipboardActions.Clip(source);
        string sub = action.Label + (truncated ? $" · 앞 {Core.Ai.ClipboardActions.MaxInput}자만" : "");
        if (Core.Ai.ClipboardActions.Local(action.Id, input) is { } local) { ShowClipResult(local, sub); return; }
        if (Ai?.Session is not { } session) return;

        Render([new SearchResult { Title = "Qutra가 처리하는 중…", Subtitle = sub, Kind = ResultKind.Calculator, Target = "", Action = ActionType.None, Score = Scores.Calculator }], keepSelection: false);
        string? output;
        try { output = await session.CompleteAsync(action.Task, input, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Error("clipboard action failed", ex); output = null; sub = ex is InvalidOperationException ? ex.Message : "Qutra가 처리하지 못했습니다"; }
        if (cts.IsCancellationRequested) return;
        ShowClipResult(output, sub);
    }

    void ShowClipResult(string? text, string sub)
    {
        _clipAiResult = text is not null;
        Render([text is null
            ? new SearchResult { Title = "결과가 없습니다", Subtitle = sub, Kind = ResultKind.Calculator, Target = "", Action = ActionType.None, Score = Scores.Calculator }
            : new SearchResult { Title = text.Length > 300 ? text[..300] + "…" : text, Subtitle = sub + " · Enter 붙여넣기 · Ctrl+C 복사", Kind = ResultKind.Calculator, Target = text, Action = ActionType.Paste, Score = Scores.Calculator }],
            keepSelection: false);
    }

    // ---------- actions ----------

    void Execute(ResultItem item)
    {
        var r = item.Result;
        if (r.Action == ActionType.None) return; // informational row
        if (_clipAiId is not null && r.Kind == ResultKind.Snippet && r.Action == ActionType.Open) { _ = RunClipActionAsync(r.Target); return; }
        if (r.Action == ActionType.Update) { _ = RunUpdateAsync(); return; }
        if (r.Action == ActionType.AiFileSearch) { _ = FindFilesWithAiAsync(r.Target); return; }
        if (r.Action == ActionType.AskAi) { _recent.Add(Query.Text); AskAi(r.Target); return; }
        if (r.Action == ActionType.Terminal) { _recent.Add(Query.Text); OpenTerminal(r.Target); return; }
        if (r.Action == ActionType.Expand)
        {
            bool expandFolders = r.Kind == ResultKind.Folder;
            HideLauncher();
            _ = ShowAndSearchExpandedAsync(_lastQuery, expandFolders, !expandFolders);
            return;
        }
        if (r.RequiresConfirmation && _armedKey != r.Key)
        {
            Disarm();
            _armedKey = r.Key;
            item.SetSubtitleOverride(ArmedText);
            return;
        }

        _recent.Add(Query.Text);
        if (r.Action == ActionType.Paste)
        {
            _ = PasteAsync(r);
            return;
        }

        if (r.Action == ActionType.Copy)
        {
            TrySetClipboard(r.Target);
            HideLauncher();
            return;
        }

        HideLauncher();
        _engine.RecordSelection(_lastQuery, r);
        try { SearchEngine.Execute(r); }
        catch (Exception ex) { ShowError("실행하지 못했습니다", r.Target, ex); }
    }

    /// <summary>Clipboard entry or snippet: put it on the clipboard, go back to the window the launcher opened over, press Ctrl+V.</summary>
    async Task PasteAsync(SearchResult r)
    {
        bool ok = r.Kind == ResultKind.Clipboard ? _clipboard.MakeCurrent(r.Target) : TrySetClipboard(r.Target);
        if (!ok) { Status.Text = "클립보드에 넣지 못했습니다"; return; }
        _engine.RecordSelection(_lastQuery, r);
        var target = _previousForeground;
        HideLauncher();
        if (target == IntPtr.Zero || target == Handle) return;
        NativeUi.ForceForeground(target);
        await Task.Delay(60); // let the target window take the focus before the keystrokes arrive
        NativeUi.SendPaste();
    }

    /// <summary>An app's installer or uninstaller chosen in the context menu: the click is the confirmation.</summary>
    void RunDirectly(SearchResult r)
    {
        _recent.Add(Query.Text);
        HideLauncher();
        try { SearchEngine.Execute(r); }
        catch (Exception ex) { ShowError("실행하지 못했습니다", r.Target, ex); }
    }

    void ShowProperties(ResultItem item)
    {
        if (item.Result.RevealPath is not { } path) return;
        HideLauncher();
        if (!NativeUi.ShowProperties(path)) Log.Info("no properties for " + path);
    }

    async Task ShowAndSearchExpandedAsync(string query, bool folders, bool files)
    {
        ShowLauncher();
        Query.Text = query;
        Query.CaretIndex = query.Length;
        await RunSearchAsync(query, folders, files);
    }

    void RunAsAdmin(ResultItem item, string path)
    {
        HideLauncher();
        _engine.RecordSelection(_lastQuery, item.Result);
        _recent.Add(Query.Text);
        try { ShellLauncher.RunAsAdmin(path); }
        catch (Exception ex) { ShowError("관리자 권한으로 실행하지 못했습니다", path, ex); }
    }

    void Reveal(ResultItem item)
    {
        if (item.Result.RevealPath is not { } path) return;
        HideLauncher();
        _engine.RecordSelection(_lastQuery, item.Result);
        _recent.Add(Query.Text);
        try { ShellLauncher.Reveal(path); }
        catch (Exception ex) { Log.Error("reveal " + path, ex); }
    }

    void ShowError(string message, string target, Exception ex)
    {
        Log.Error($"{message}: {target}", ex);
        ShowLauncher();
        Status.Text = $"{message}: {ex.Message}";
    }

    void CopyPath(ResultItem item)
    {
        if (TrySetClipboard(item.Result.RevealPath ?? item.Result.Target)) Status.Text = "경로를 복사했습니다";
    }

    static bool TrySetClipboard(string text)
    {
        // The clipboard can be briefly locked by another process.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return false;
    }

    string? _updateStatus;
    DateTime _updateCheckedUtc = DateTime.MinValue;
    CancellationTokenSource? _updateCts;
    bool _updateBusy;

    /// <summary>Shows update progress on the "update" row, including rows recreated by later search stages.</summary>
    void SetUpdateStatus(string text)
    {
        _updateStatus = text;
        foreach (var i in _items) if (i.Result.Action == ActionType.Update) i.SetSubtitleOverride(text);
    }

    /// <summary>
    /// Runs when the "update" row appears, i.e. "update" / "업데이트" was typed: asks GitHub right away and, when
    /// there is a newer release, downloads and installs it without waiting for Enter.
    /// </summary>
    async Task CheckForUpdateAsync()
    {
        // Each search stage re-renders the row; one check per command is enough.
        if (_updateBusy || DateTime.UtcNow - _updateCheckedUtc < TimeSpan.FromSeconds(15)) return;
        _updateBusy = true;
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        bool install = false;
        try
        {
            SetUpdateStatus("GitHub에서 최신 버전을 확인하는 중…");
            var latest = await _updates.CheckAsync(_updateCts.Token);
            var current = Updater.CurrentVersion.ToString(3);
            _updateCheckedUtc = DateTime.UtcNow;
            install = _updates.Available is not null;
            SetUpdateStatus(latest is null
                ? $"GitHub({_settings.UpdateRepository})에 Quicklight_v버전.zip이 든 릴리스가 없습니다 (지금 v{current})"
                : install
                    ? $"새 버전 v{latest.Version.ToString(3)} 있음 (지금 v{current})   ·   설치를 시작합니다"
                    : $"최신 버전입니다 (v{current})");
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus(_updateCts?.IsCancellationRequested == true ? "확인을 취소했습니다" : "GitHub 응답이 너무 늦습니다. 잠시 뒤 다시 시도하세요");
        }
        catch (Exception ex) when (ex is UpdateException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            Log.Error("update check failed", ex);
            SetUpdateStatus("업데이트를 확인하지 못했습니다: " + ex.Message);
        }
        finally { _updateBusy = false; }
        if (install) await RunUpdateAsync();
    }

    /// <summary>Downloads (or reuses the background download of) the newer release, then restarts into it.</summary>
    async Task RunUpdateAsync()
    {
        if (_updateBusy) return;
        if (_updates.Available is not { } release) { _updateCheckedUtc = DateTime.MinValue; await CheckForUpdateAsync(); return; }
        _updateBusy = true;
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        try
        {
            var version = release.Version.ToString(3);
            var progress = new Progress<double>(p => SetUpdateStatus($"v{version} 내려받는 중… {p:P0}   ·   Esc로 취소"));
            var downloaded = await _updates.DownloadAsync(release, progress, "Esc로 취소", _updateCts.Token);
            SetUpdateStatus($"v{version}(으)로 다시 시작합니다…");
            await Task.Delay(400, _updateCts.Token); // Esc in this moment still cancels
            _updates.Install(downloaded);
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus(_updateCts?.IsCancellationRequested == true ? "업데이트를 취소했습니다" : "GitHub 응답이 너무 늦어 업데이트하지 못했습니다");
        }
        catch (Exception ex) when (ex is UpdateException or System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            Log.Error("update failed", ex);
            SetUpdateStatus("업데이트하지 못했습니다: " + ex.Message);
        }
        finally { _updateBusy = false; }
    }
    const string ArmedText = "한 번 더 Enter를 누르면 실행합니다 · Esc로 취소";

    void Disarm()
    {
        if (_armedKey is null) return;
        foreach (var i in _items.Concat(_parts)) if (i.Result.Key == _armedKey) i.SetSubtitleOverride(null);
        _armedKey = null;
    }

    void UpdateFooter()
    {
        if (_previewOpen)
        {
            FooterText.Text = _parts.Count > 0 ? "↑↓  항목 선택      ↵  실행      우클릭  더 보기      Space / Esc  닫기"
                : IsGrid ? "Space / Esc  닫기" : "Space / Esc  닫기      ↑↓  다른 항목 미리보기";
            return;
        }
        if (_terminalOpen) { FooterText.Text = "↵  실행      Ctrl+C  중단      Esc  검색창으로      Esc 두 번  닫기"; return; }
        if (Selected is not { } item) { FooterText.Text = ""; return; }
        var r = item.Result;
        if (IsGrid)
        {
            FooterText.Text = $"앱 {_apps.Count}개      ↵  실행      ←↑↓→  이동      우클릭  더 보기      Esc  돌아가기";
            return;
        }
        if (r.Action == ActionType.None) { FooterText.Text = "Esc  닫기"; return; }
        if (r.Action == ActionType.Update) { FooterText.Text = "↵  다시 확인      Esc  취소 / 닫기"; return; }
        if (r.Action == ActionType.Expand) { FooterText.Text = "↵  시스템 항목 펼치기      Esc  닫기"; return; }
        string enter = r.Action switch
        {
            ActionType.Copy => "결과 복사",
            ActionType.System => "실행",
            ActionType.Paste => "붙여넣기",
            ActionType.SwitchWindow => "창으로 전환",
            ActionType.Kill => "종료 (두 번)",
            ActionType.Terminal => "터미널에서 실행",
            ActionType.AskAi => "Qutra에게 묻기",
            ActionType.AiFileSearch => "검색어 만들기",
            _ => r.Kind switch
            {
                ResultKind.App => "실행",
                ResultKind.Url or ResultKind.WebSearch => "브라우저에서 열기",
                _ => "열기",
            },
        };
        var parts = new List<string> { $"↵  {enter}" };
        if (r.RevealPath is not null) parts.Add("Ctrl+↵  폴더에서 보기");
        if (CanPreview(r)) parts.Add("Space 길게  미리보기");
        else if (r.Kind is ResultKind.Url) parts.Add("Ctrl+Shift+C  경로 복사");
        if (r.Kind == ResultKind.Clipboard) parts.Add("Del  기록에서 삭제");
        if (r.Kind == ResultKind.Clipboard && _clipboard.Text(r.Target) is not null && ClipActionsAvailable) parts.Add("Tab  Qutra 작업");
        if (r.RevealPath is not null) parts.Add("Alt+↵  속성      우클릭  더 보기");
        parts.Add("Esc  닫기");
        FooterText.Text = string.Join("      ", parts);
    }

    // ---------- mini terminal ----------

    // ---------- local AI ----------

    /// <summary>Set by the app: the AI side (conversations, the answer panel). Null until it is created.</summary>
    public Siri.AiHost? Ai { get; set; }
    bool _handingOffToAi;

    /// <summary>Adds the "Qutra로 파일 찾기" row below the top result for a file description (always for several words in the Files scope).</summary>
    IReadOnlyList<SearchResult> WithAiFileRow(IReadOnlyList<SearchResult> results)
    {
        if (Ai is null || !_settings.AiEnabled || !Core.Ai.AiModel.IsAvailable) return results;
        if (!(Core.Ai.FileQueryDetector.IsFileQuery(_lastQuery) || _scope == Scope.Files && _lastQuery.Contains(' '))) return results;
        var row = new SearchResult { Title = "Qutra로 파일 찾기", Subtitle = _lastQuery, Kind = ResultKind.Command, Target = _lastQuery, Action = ActionType.AiFileSearch, Score = 0 };
        var list = results.ToList();
        list.Insert(Math.Min(1, list.Count), row);
        return list;
    }

    /// <summary>Asks the model for one Everything query and puts it in the search box, where it runs and can be edited.</summary>
    async Task FindFilesWithAiAsync(string description)
    {
        if (Ai is not { } ai) return;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        Render([new SearchResult { Title = "Qutra가 검색어를 만드는 중…", Subtitle = description, Kind = ResultKind.Command, Target = "", Action = ActionType.None }], keepSelection: false);
        try
        {
            await Task.Delay(600, token);
            var q = Core.Ai.FileQueryDetector.CleanQuery(await ai.Session.CompleteAsync(Core.Ai.Prompts.FileQuery(DateTime.Today), description, token));
            if (token.IsCancellationRequested) return;
            if (q is null) { Status.Text = "Qutra가 검색어를 만들지 못했습니다"; return; }
            Query.Text = q; // runs as a normal search; the user sees the query and can edit it
            Query.CaretIndex = q.Length;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("ai file search failed", ex); Status.Text = "Qutra가 검색어를 만들지 못했습니다"; }
    }

    void AskAiWarmUp() => _ = Ai?.Session.WarmUpAsync().ContinueWith(t => { if (t.IsFaulted) Log.Error("ai warm-up failed", t.Exception!.GetBaseException()); });

    /// <summary>The question leaves the launcher: its capsule closes and the status capsule takes its place on screen.</summary>
    void AskAi(string question)
    {
        if (Ai is null) return;
        var origin = BarScreenRect();
        var returnTo = _previousForeground;
        _handingOffToAi = true;
        try
        {
            if (Ai.Ask(question, origin, returnTo, Tint.Background)) HideLauncher(aiHandoff: true);
            else if (Ai.IsBusy) Status.Text = "Qutra가 아직 답하는 중입니다"; // Enter must not look dead
        }
        finally { _handingOffToAi = false; }
    }

    /// <summary>The search capsule's rectangle on screen, in physical pixels.</summary>
    Rect BarScreenRect()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var p = BarPanel.PointToScreen(new Point(0, 0));
        return new Rect(p.X, p.Y, BarPanel.ActualWidth * scale, BarPanel.ActualHeight * scale);
    }

    bool _terminalOpen;
    string _terminalDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    System.Diagnostics.Process? _terminalProcess;

    void OpenTerminal(string command)
    {
        ClosePreview();
        _terminalOpen = true;
        Results.Visibility = AppGrid.Visibility = Visibility.Collapsed;
        TerminalPanel.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        UpdateFooter();
        ShowTerminalDir();
        SyncCardWidth();
        RunTerminal(command);
        TerminalInput.Focus();
        Keyboard.Focus(TerminalInput);
    }

    /// <summary>The current folder in front of the input line, like a shell prompt.</summary>
    void ShowTerminalDir()
    {
        TerminalPrompt.Text = _terminalDir + ">";
        TerminalPrompt.ToolTip = _terminalDir;
    }

    void CloseTerminal()
    {
        if (!_terminalOpen) return;
        _terminalOpen = false;
        _termAi?.Cancel();
        StopTerminalProcess();
        TerminalPanel.Visibility = Visibility.Collapsed;
        TerminalOutput.Clear();
        TerminalInput.Clear();
        if (IsGrid) AppGrid.Visibility = Visibility.Visible;
        else Results.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncCardWidth();
        UpdateFooter();
    }

    void StopTerminalProcess()
    {
        var p = _terminalProcess;
        _terminalProcess = null;
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: // back to the search box; the next Esc there closes the launcher
                _termAi?.Cancel();
                Query.Focus();
                Keyboard.Focus(Query);
                e.Handled = true; break;
            case Key.Enter when TerminalInput.Text.Trim() is { Length: > 0 } command:
                if (command.StartsWith('?') && TerminalAiReady) { TerminalInput.Clear(); _ = TerminalAskAiAsync(command); }
                else { TerminalInput.Clear(); RunTerminal(command); }
                e.Handled = true; break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && TerminalInput.SelectionLength == 0:
                _termAi?.Cancel();
                StopTerminalProcess();
                e.Handled = true; break;
        }
    }

    // ---- terminal helper (Qutra): "? request" makes a command, "??" explains the last error ----
    CancellationTokenSource? _termAi;
    string _termCmd = "";
    readonly System.Text.StringBuilder _termOut = new();

    bool TerminalAiReady => _settings.AiEnabled && Core.Ai.AiModel.IsAvailable && Ai is not null;

    async Task TerminalAskAiAsync(string input)
    {
        _termAi?.Cancel();
        var cts = _termAi = new CancellationTokenSource();
        bool explain = input.StartsWith("??");
        var request = input.TrimStart('?').Trim();
        if (!explain && request.Length == 0) return;
        if (explain && _termCmd.Length == 0) { TerminalWrite("설명할 명령이 없습니다.\n"); return; }
        TerminalWrite(explain ? "Qutra가 오류를 살펴보는 중…\n" : "Qutra가 명령을 만드는 중…\n");
        try
        {
            var session = Ai!.Session;
            string answer = explain
                ? await session.CompleteAsync(Core.Ai.Prompts.TerminalExplain, Core.Ai.TerminalAi.ExplainUser(_termCmd, _termOut.ToString()), cts.Token) ?? ""
                : Core.Ai.TerminalAi.CleanCommand(await session.CompleteAsync(Core.Ai.Prompts.TerminalCommand, Core.Ai.TerminalAi.CommandUser(_terminalDir, request), cts.Token) ?? "");
            if (!_terminalOpen || cts.IsCancellationRequested) return;
            if (answer.Length == 0) { TerminalWrite("Qutra가 답하지 못했습니다.\n"); return; }
            if (explain) TerminalWrite("Qutra: " + answer + "\n");
            else
            {
                TerminalWrite("제안된 명령입니다. 확인하고 Enter로 실행하세요.\n");
                TerminalInput.Text = answer;
                TerminalInput.SelectAll();
                TerminalInput.Focus();
            }
        }
        catch (OperationCanceledException) { if (_terminalOpen) TerminalWrite("취소했습니다.\n"); }
        catch (Exception ex) { Log.Error("terminal ai failed", ex); if (_terminalOpen) TerminalWrite("Qutra 요청에 실패했습니다.\n"); }
    }

    void TerminalWrite(string text)
    {
        const int Keep = 200_000;
        TerminalOutput.AppendText(text);
        if (TerminalOutput.Text.Length > Keep) TerminalOutput.Text = TerminalOutput.Text[^(Keep / 2)..];
        TerminalOutput.ScrollToEnd();
    }

    /// <summary>
    /// Runs one line through cmd.exe in the terminal's folder (<c>cd</c> moves it, <c>cls</c> clears). Input is closed,
    /// so a program that waits for typing ends instead of hanging; Ctrl+C or closing the terminal kills it.
    /// </summary>
    void RunTerminal(string line)
    {
        StopTerminalProcess();
        TerminalWrite($"{_terminalDir}> {line}\n");
        var cd = System.Text.RegularExpressions.Regex.Match(line, @"^cd(?:\s+/d)?(?:\s+(.+))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (cd.Success)
        {
            if (!cd.Groups[1].Success) TerminalWrite(_terminalDir + "\n");
            else
            {
                string target;
                try { target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cd.Groups[1].Value.Trim().Trim('"')), _terminalDir); }
                catch (ArgumentException) { target = ""; }
                if (Directory.Exists(target)) { _terminalDir = target; ShowTerminalDir(); }
                else TerminalWrite("지정된 경로를 찾을 수 없습니다.\n");
            }
            return;
        }
        if (line.Equals("cls", StringComparison.OrdinalIgnoreCase)) { TerminalOutput.Clear(); return; }

        // UTF-8 (code page 65001) rather than the OEM page: "tree" draws its lines with characters the Korean OEM page turns into "??".
        var utf8 = new System.Text.UTF8Encoding(false);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /s /c \"chcp 65001 >nul & " + line + "\"", WorkingDirectory = _terminalDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = utf8, StandardErrorEncoding = utf8,
        };
        try
        {
            var p = _terminalProcess = new System.Diagnostics.Process { StartInfo = psi };
            // A command like "tree" on C:\ prints without end: past these limits the program is stopped, so neither the
            // UI queue nor the text box can grow without bound. Counted on the reader threads, before anything is queued.
            const int MaxLines = 3000, MaxChars = 100_000;
            int lines = 0, chars = 0, stopped = 0;
            void OnLine(object _, System.Diagnostics.DataReceivedEventArgs d)
            {
                if (d.Data is not { } text || Volatile.Read(ref stopped) == 1) return;
                if (Interlocked.Increment(ref lines) > MaxLines | Interlocked.Add(ref chars, text.Length + 1) > MaxChars)
                {
                    if (Interlocked.Exchange(ref stopped, 1) == 0) // the first reader to pass the limit stops it, once
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (_terminalProcess != p) return;
                            StopTerminalProcess();
                            TerminalWrite($"\n출력이 너무 많아 중단했습니다 ({MaxLines:N0}줄 또는 {MaxChars:N0}자 초과). 범위를 좁혀 다시 실행하세요.\n");
                        });
                    return;
                }
                Dispatcher.BeginInvoke(() => { if (_terminalProcess == p) { TerminalWrite(text + "\n"); _termOut.AppendLine(text); } });
            }
            _termCmd = line; _termOut.Clear();
            p.OutputDataReceived += OnLine;
            p.ErrorDataReceived += OnLine;
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (TerminalAiReady)
                _ = Task.Run(() =>
                {
                    try { p.WaitForExit(); } catch (InvalidOperationException) { return; }
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_terminalProcess != p || !Core.Ai.TerminalAi.LooksFailed(p.ExitCode, _termOut.ToString())) return;
                        TerminalWrite("?? 를 입력하면 Qutra가 오류를 설명합니다\n");
                    });
                });
        }
        catch (Exception ex) { TerminalWrite(ex.Message + "\n"); }
    }

    // ---------- preview (hold Space) ----------

    const double PreviewHoldMs = 1000;
    const double PullDelayMs = 150; // a tap (typing a space) should not make the row twitch
    readonly System.Windows.Threading.DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewHoldMs) };
    bool _holding;
    ListBoxItem? _pulledRow;
    bool _previewOpen;
    int _previewGeneration;
    readonly Dictionary<string, string> _summaries = []; // path + last write time -> Qutra's summary, for the session
    bool _summaryMode;
    CancellationTokenSource? _summaryCts;

    static bool CanPreview(SearchResult r) => ResultItem.CanPreview(r);

    /// <summary>Space went down on a previewable row: start the timer and stretch the row as if it were being pulled.</summary>
    void StartHold(ResultItem item)
    {
        _holding = true;
        _holdTimer.Start();
        if (!UiMotion.Enabled) return;
        var list = ActiveList;
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row) return;
        _pulledRow = row;
        var scale = new ScaleTransform();
        row.RenderTransformOrigin = new Point(0.5, 0.5);
        row.RenderTransform = scale;
        if (list.Template.FindName("Glass", list) is Grid glass) glass.RenderTransform = scale; // same size and center as the row
        // The row swells the longer Space is held, as if about to pop open into the preview.
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        AnimationTimeline Pull(double to)
        {
            var a = Anim(null, to, PreviewHoldMs - PullDelayMs, ease);
            a.BeginTime = TimeSpan.FromMilliseconds(PullDelayMs);
            return a;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Pull(1.04));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Pull(1.06));
    }

    void CancelHold()
    {
        _holding = false;
        _holdTimer.Stop();
        SpringBack();
    }

    /// <summary>Returns the held row to its resting size.</summary>
    void SpringBack()
    {
        if (_pulledRow?.RenderTransform is not ScaleTransform scale) return;
        _pulledRow = null;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 1, UiMotion.Standard, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 1, UiMotion.Standard, ease));
    }

    void OpenPreview()
    {
        if (Selected is not { } item || !CanPreview(item.Result)) return;
        CancelSummary();
        var path = item.Result.RevealPath;
        bool wasOpen = _previewOpen;
        _previewOpen = true;
        int generation = ++_previewGeneration;
        PreviewTitle.Text = item.Title;
        PreviewInfo.Text = path ?? "";
        PreviewImage.Source = null;
        PreviewText.Text = "";
        PreviewText.TextWrapping = TextWrapping.NoWrap;
        _parts.Clear();
        PreviewParts.Visibility = Visibility.Collapsed;
        ActiveList.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;
        UpdateFooter();
        if (!wasOpen && UiMotion.Enabled)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.985, 1, UiMotion.Standard, ease));
            PreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.985, 1, UiMotion.Standard, ease));
            PreviewPanel.BeginAnimation(OpacityProperty, Anim(0, 1, UiMotion.Standard, ease));
        }
        if (item.Result.App is { } app) ShowAppDetails(app);
        else if (item.IsClip) _ = ShowClipAsync(item, generation);
        else _ = LoadPreviewAsync(path!, generation);
    }

    /// <summary>A clipboard entry in full: all of its text, wrapped, or its image at full size.</summary>
    async Task ShowClipAsync(ResultItem item, int generation)
    {
        string when = item.Result.Subtitle.Replace("클립보드 · ", "");
        if (_clipboard.Text(item.Result.Target) is { } text)
        {
            int lines = text.ReplaceLineEndings("\n").Split('\n').Length;
            PreviewTitle.Text = "텍스트";
            PreviewInfo.Text = $"{when} · {text.Length:N0}자 · {lines:N0}줄";
            PreviewText.TextWrapping = TextWrapping.Wrap;
            PreviewText.Text = text.Length > 200_000 ? text[..200_000] + "\n…" : text;
            PreviewText.Visibility = Visibility.Visible;
            return;
        }
        PreviewTitle.Text = "이미지";
        PreviewInfo.Text = when;
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewImage.Source = item.Icon; // the card's thumbnail until the full image is in
        var image = await _clipboard.ImageAsync(item.Result.Target);
        if (generation != _previewGeneration || image is null) return;
        PreviewImage.Source = image;
    }

    /// <summary>The app's parts as rows, like apps themselves: what Enter on the app runs first, then its other shortcuts, program, tools, installer and uninstaller.</summary>
    void ShowAppDetails(AppEntry app)
    {
        PreviewInfo.Text = string.Join(" · ", new[] { "애플리케이션", app.Publisher, app.Version is { } v ? "버전 " + v : app.Location }.OfType<string>());
        PreviewText.Visibility = Visibility.Collapsed;
        var launch = app.Launch;
        int generation = _icons.NextGeneration();
        foreach (var part in app.Parts.OrderBy(p => p != launch).ThenBy(p => p.Kind))
        {
            var row = new ResultItem(AppProvider.PartResult(app, part), "");
            _parts.Add(row);
            LoadIcon(row, generation);
        }
        PreviewParts.Visibility = Visibility.Visible;
        SelectIn(PreviewParts, 0);
    }

    void ClosePreview()
    {
        if (!_previewOpen) return;
        _previewOpen = false;
        _previewGeneration++;
        CancelSummary();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewText.Text = "";
        _parts.Clear();
        PreviewParts.Visibility = Visibility.Collapsed;
        if (IsGrid) AppGrid.Visibility = Visibility.Visible;
        else Results.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooter();
    }


    bool CanSummarize(string path) => _settings.AiEnabled && Core.Ai.AiModel.IsAvailable && Ai is not null && File.Exists(path) && Core.Ai.DocumentText.Supports(path);

    /// <summary>The previewed file's path when Tab can summarize it now; null for an app, a folder, clipboard or no Qutra.</summary>
    string? SummarizablePath() => _previewOpen && _parts.Count == 0 && Selected?.Result.RevealPath is { } p && CanSummarize(p) ? p : null;

    void CancelSummary()
    {
        _summaryMode = false;
        _summaryCts?.Cancel();
        _summaryCts = null;
    }

    /// <summary>Tab on a file preview: Qutra's summary in the preview text, cached per file version. Moving or closing cancels it.</summary>
    async Task ShowSummaryAsync(string path)
    {
        CancelSummary();
        _summaryMode = true;
        var cts = _summaryCts = new CancellationTokenSource();
        int generation = _previewGeneration;
        string key = path + "|" + new FileInfo(path).LastWriteTimeUtc.Ticks;
        PreviewImage.Source = null;
        PreviewText.TextWrapping = TextWrapping.Wrap;
        PreviewText.Visibility = Visibility.Visible;
        if (_summaries.TryGetValue(key, out var cached)) { PreviewText.Text = cached; return; }
        PreviewText.Text = "Qutra가 요약하는 중…";
        try
        {
            string? body = await Task.Run(() => Core.Ai.DocumentText.Extract(path));
            string? summary = null;
            if (body is not null && Ai?.Session is { } session) summary = await session.CompleteAsync(Core.Ai.Prompts.Summary, body, cts.Token);
            if (cts.IsCancellationRequested || generation != _previewGeneration) return;
            if (summary is null) { PreviewText.Text = "요약하지 못했습니다"; return; }
            _summaries[key] = summary;
            PreviewText.Text = summary;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("summary failed", ex);
            if (!cts.IsCancellationRequested) PreviewText.Text = "요약하지 못했습니다";
        }
    }

    async Task LoadPreviewAsync(string path, int generation)
    {
        string? text = null;
        ImageSource? image = null;
        string info = path;
        try
        {
            if (Directory.Exists(path))
            {
                var entries = await Task.Run(() => new DirectoryInfo(path).EnumerateFileSystemInfos().Take(500).ToList());
                text = entries.Count == 0 ? "(비어 있음)"
                    : string.Join("\n", entries.OrderByDescending(i => i is DirectoryInfo).ThenBy(i => i.Name)
                        .Select(i => (i is DirectoryInfo ? "▸ " : "   ") + i.Name));
                info = $"폴더 · 항목 {entries.Count}{(entries.Count == 500 ? "+" : "")}개 · {path}";
            }
            else
            {
                var file = new FileInfo(path);
                info = $"{FileTypeLabel.For(file.Name, false)} · {FormatSize(file.Length)} · 수정 {file.LastWriteTime:yyyy-MM-dd HH:mm}";
                if (FileTypeLabel.IsPlainText(path)) text = await Task.Run(() => ReadTextHead(path));
                else if (FileTypeLabel.IsImage(path)) image = await Task.Run(() => LoadImage(path));
                else if (await ThumbnailAsync(path, generation) is { } px)
                    image = Frozen(BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32, null, px.Bgra, px.Width * 4));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or System.Security.SecurityException or InvalidOperationException
                                       or FormatException or System.Runtime.InteropServices.COMException) // corrupt images
        {
            text = "미리 볼 수 없습니다: " + ex.Message;
        }
        if (generation != _previewGeneration) return; // closed, or moved on to another item
        text ??= image is null ? "미리 볼 수 없는 형식입니다" : null;
        PreviewInfo.Text = CanSummarize(path) ? info + " · Tab: Qutra 요약" : info;
        PreviewText.Text = text ?? "";
        PreviewText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewImage.Source = image;
    }

    /// <summary>The first lines of a text file, as UTF-8 or, when that fails, the Korean ANSI code page (CP949).</summary>
    static string ReadTextHead(string path)
    {
        var buffer = new byte[64 * 1024];
        int n;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = f.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (n == buffer.Length)
        {
            // Cut at 64 KB: drop the last character, which may be a split UTF-8 sequence that would fail the decode.
            int i = n - 1;
            while (i > n - 4 && (buffer[i] & 0xC0) == 0x80) i--;
            if (buffer[i] >= 0xC0) n = i;
        }
        string text;
        try { text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, n); }
        catch (System.Text.DecoderFallbackException)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            text = System.Text.Encoding.GetEncoding(949).GetString(buffer, 0, n);
        }
        return string.Join("\n", text.TrimStart('﻿').ReplaceLineEndings("\n").Split('\n').Take(300));
    }

    static ImageSource LoadImage(string path)
    {
        int height;
        using (var s = File.OpenRead(path))
            height = BitmapFrame.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelHeight;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (height > 900) bmp.DecodePixelHeight = 900; // only shrink: decoding a small image at 900 px blows it up
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        return Frozen(bmp);
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    Task<IconPixels?>? _thumbnailRun;
    long _thumbnailStartedAt;

    /// <summary>
    /// One thumbnail at a time, the latest request wins: ↑↓ through a folder of videos must not start a thread per file.
    /// A shell extension that hangs keeps its thread; after a few seconds the next preview gets a fresh one.
    /// </summary>
    async Task<IconPixels?> ThumbnailAsync(string path, int generation)
    {
        while (_thumbnailRun is { IsCompleted: false } running && Environment.TickCount64 - _thumbnailStartedAt < 3000)
        {
            await Task.WhenAny(running, Task.Delay(100));
            if (generation != _previewGeneration) return null; // moved on meanwhile
        }
        _thumbnailStartedAt = Environment.TickCount64;
        var run = _thumbnailRun = RunSta(() => ShellIcons.Get(path, 512, thumbnail: true));
        return await run;
    }

    /// <summary>Shell thumbnails are COM and want an STA thread; the UI thread must not wait on a slow video thumbnail.</summary>
    static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Preview thumbnail" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ---------- Everything ----------

    /// <summary>
    /// While the launcher is open, watches whether Everything answers (its indexing itself is not shown).
    /// </summary>
    async Task PollIndexAsync()
    {
        var state = _settings.FileSearch ? await Task.Run(() => EverythingClient.GetDbState(200)) : EverythingClient.DbState.NotRunning;
        _unresponsivePolls = state == EverythingClient.DbState.Unresponsive ? _unresponsivePolls + 1 : 0;
        ShowEverythingStatus(state);
    }

    async Task OpenEverythingOptionsAsync()
    {
        if (!EverythingClient.IsAvailable)
        {
            Status.Text = "Everything을 시작하는 중…";
            var outcome = await EverythingBootstrap.EnsureRunningAsync(_settings);
            if (outcome is not (EverythingBootstrap.Outcome.AlreadyRunning or EverythingBootstrap.Outcome.Started))
            {
                Status.Text = outcome == EverythingBootstrap.Outcome.Disabled ? "파일 검색이 꺼져 있습니다" : "Everything을 시작하지 못했습니다";
                return;
            }
            Status.Text = "";
        }
        if (EverythingClient.OpenOptions()) HideLauncher(); // get out of the way of the dialog (the launcher is topmost)
        else Status.Text = "Everything 설정을 열지 못했습니다";
    }

    /// <summary>Checks Everything off the UI thread: a busy Everything must not delay the first paint.</summary>
    const string EverythingDown = "Everything이 실행 중이 아님 · 파일 결과 제외";
    const string EverythingStalled = "Everything 응답 없음 · 파일 결과 잠시 제외";

    /// <summary>
    /// Says so while file results are missing, from the once-a-second poll: at once when Everything is not running,
    /// after about five seconds when it stops answering (it answers slowly while busy with a big index or a burst of
    /// file changes), and gone again as soon as it answers. Only our own messages are touched.
    /// </summary>
    void ShowEverythingStatus(EverythingClient.DbState state)
    {
        string? text = !_settings.FileSearch ? null
            : state == EverythingClient.DbState.NotRunning ? EverythingDown
            : _unresponsivePolls >= 5 ? EverythingStalled
            : null;
        bool ours = Status.Text is EverythingDown or EverythingStalled or "";
        if (ours) Status.Text = text ?? "";
    }
}

static class ListExtensions
{
    public static int FindIndex<T>(this List<T> list, Func<T, bool> pred) => list.FindIndex(new Predicate<T>(pred));
}
