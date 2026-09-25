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
using Quicklight.Core.Ai;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Core.Update;

namespace Quicklight;

public partial class MainWindow : Window
{
    const string TopHitGroup = ResultItem.HeroGroup;
    const double ShadowMargin = 24;      // DIPs around the panel, room for the drop shadow (matches Shell.Margin)
    const double MaxPanelHeight = 660;   // DIPs; the bar sits so that a fully expanded panel is centered on screen
    const double PanelRadius = 28;       // corner radius once the panel expands; the bare search bar is a pill
    const double BackdropPad = 48;       // DIPs captured beyond the panel so the blur near its edges has real content
    const double BackdropBlur = 14;      // blur strength (Gaussian sigma) of the backdrop, in DIPs

    readonly SearchEngine _engine;
    readonly QuicklightSettings _settings;
    readonly UpdateService _updates;
    readonly ClipboardHistoryProvider _clipboard;
    IntPtr _previousForeground; // where Paste results go
    readonly IconLoader _icons = new(64);
    readonly ObservableCollection<ResultItem> _items = [];
    readonly ObservableCollection<ResultItem> _apps = [];
    readonly ObservableCollection<ActivityItem> _activities = [];
    readonly System.Windows.Threading.DispatcherTimer _indexPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    ActivityTracker.Handle? _indexActivity;
    int _indexBusyPolls;
    int _activitySyncQueued;
    IReadOnlyList<AppEntry>? _catalogSource;
    CancellationTokenSource? _cts;
    string? _armedKey; // result waiting for the second Enter of a destructive command
    string _lastQuery = "";
    bool _hiding;
    int _frameRate = 60; // refresh rate of the monitor the launcher is on; animations run at this rate

    public IntPtr Handle { get; private set; }

    /// <summary>Keep the window open when it loses focus (--pinned, for screenshots and debugging).</summary>
    public bool Pinned { get; set; }

    public MainWindow(SearchEngine engine, QuicklightSettings settings, UpdateService updates, ClipboardHistoryProvider clipboard)
    {
        _engine = engine;
        _clipboard = clipboard;
        _settings = settings;
        _updates = updates;
        InitializeComponent();
        ((CollectionViewSource)Resources["GroupedResults"]).Source = _items;
        ((CollectionViewSource)Resources["GroupedApps"]).Source = _apps;
        Deactivated += (_, _) => { if (!Pinned) HideLauncher(); };
        Activities.ItemsSource = _activities;
        ActivityTracker.Shared.Changed += QueueActivitySync;
        _indexPoll.Tick += (_, _) => _ = PollIndexAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _indexPoll.Start(); _ = PollIndexAsync(); }
            else _indexPoll.Stop();
        };
        SyncActivities();
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            SpringBack();
            OpenPreview();
        };
        BuildDots();
        _ai = new AiSession(settings);
        _ai.Line += (kind, text) => Dispatcher.BeginInvoke(() => OnAiLine(kind, text));
        _ai.AnswerDone += () => Dispatcher.BeginInvoke(OnAiDone);
        _ai.Failed += msg => Dispatcher.BeginInvoke(() => { if (_aiBusy) { OnAiLine(AiLineKind.Error, msg); OnAiDone(); } });
    }

    // ---------- AI conversation ----------
    // As in Siri: after Enter the bar gathers into a small pill with turning dots and what the AI is doing; when the
    // answer is ready the conversation opens above, and follow-ups are typed in the box below it.

    readonly AiSession _ai;
    bool _aiMode;
    bool _aiBusy;
    bool _compact; // the panel is the thinking pill
    TextBlock? _aiAnswer; // the answer being written
    double _backdropLeft; // backdrop offset for the full-width panel

    // Spring-like: quick start, long gentle settle, so steps flow into each other instead of snapping.
    static readonly IEasingFunction Settle = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
    const double MoveMs = 560;

    /// <summary>Six dots on a circle, fading around it, for the spinner.</summary>
    void BuildDots()
    {
        for (int i = 0; i < 6; i++)
        {
            double a = i * Math.PI / 3;
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Opacity = 0.2 + i * 0.16 };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            Canvas.SetLeft(dot, 13 + 9 * Math.Cos(a) - 2.5);
            Canvas.SetTop(dot, 13 + 9 * Math.Sin(a) - 2.5);
            Dots.Children.Add(dot);
        }
    }

    double FullWidth => Width - 2 * ShadowMargin;

    void Fade(UIElement e, double to, double ms = 260) => e.BeginAnimation(OpacityProperty, Anim(null, to, ms, Settle));

    /// <summary>Animates the panel width: to <paramref name="width"/>, or back to full width when null.</summary>
    void AnimateWidth(double? width)
    {
        double from = Shell.ActualWidth > 0 ? Shell.ActualWidth : FullWidth;
        double to = width ?? FullWidth;
        var anim = Anim(from, to, MoveMs, Settle);
        if (width is null)
            anim.Completed += (_, _) =>
            {
                if (_compact) return; // narrowed again meanwhile
                Shell.BeginAnimation(WidthProperty, null);
                Shell.Width = double.NaN;
            };
        Shell.Width = to;
        Shell.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>The pill just fits the dots and the text, so a longer text widens it.</summary>
    double PillWidth()
    {
        Thinking.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Clamp(Thinking.DesiredSize.Width + 30 + 28 + 48, 180, FullWidth);
    }

    void StartAi(string question)
    {
        _cts?.Cancel();
        _aiMode = true;
        AiLog.Children.Clear();
        Results.Visibility = Footer.Visibility = AppGrid.Visibility = AiPanel.Visibility = Visibility.Collapsed;
        Placeholder.Text = "이어서 질문하기";
        AskHint.Visibility = Visibility.Collapsed;
        Query.Clear();
        _compact = true;
        AddTurn(question);
        UpdateShape();
        _ai.Ask(question);
    }

    /// <summary>Shows the dots and <paramref name="text"/> in place of the search box; null ends it.</summary>
    void SetAiBusy(string? text)
    {
        var flow = (RotateTransform)((LinearGradientBrush)Resources["AiFlowBrush"]).RelativeTransform;
        // The box stays (invisible) so it keeps keyboard focus: Esc and typing still work.
        foreach (var e in new UIElement[] { GlyphBox, Query, Placeholder }) Fade(e, text is null ? 1 : 0);
        if (text is null)
        {
            _thinking = false;
            var hide = Anim(null, 0, 220, Settle);
            hide.Completed += (_, _) =>
            {
                if (_thinking) return; // shown again before the fade ended
                Thinking.Visibility = AiGlow.Visibility = Visibility.Collapsed;
                Dots.BeginAnimation(OpacityProperty, null);
                DotsSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                flow.BeginAnimation(RotateTransform.AngleProperty, null);
            };
            Thinking.BeginAnimation(OpacityProperty, hide);
            Fade(AiGlow, 0, 400);
            return;
        }
        if (!_thinking)
        {
            // First step: text in place, then everything fades in while the panel gathers into the pill.
            _thinking = true;
            AiBusyText.Text = text;
            AiBusyText.BeginAnimation(OpacityProperty, null);
            AiBusyText.Opacity = 1;
            Thinking.Visibility = AiGlow.Visibility = Visibility.Visible;
            Thinking.Opacity = 0;
            Fade(Thinking, 1, 360);
            Fade(AiGlow, 0.8, 500);
            Dots.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            DotsSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
            flow.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.2)) { RepeatBehavior = RepeatBehavior.Forever });
            if (_compact) AnimateWidth(PillWidth());
            return;
        }
        if (AiBusyText.Text == text) return;
        // A new step: the old words fade out, the pill resizes, the new words fade in.
        var fadeOut = Anim(null, 0, 150, Settle);
        fadeOut.Completed += (_, _) =>
        {
            AiBusyText.Text = text;
            if (_compact) AnimateWidth(PillWidth());
            Fade(AiBusyText, 1, 320);
        };
        AiBusyText.BeginAnimation(OpacityProperty, fadeOut);
    }
    bool _thinking; // the dots and words are shown (or fading in)

    /// <summary>Spotlight's "— Ask Siri": the hint sits right after the typed question while asking is the top result.</summary>
    void UpdateAskHint(bool show)
    {
        if (!show || _aiMode || Query.Text.Length == 0) { AskHint.Visibility = Visibility.Collapsed; return; }
        var typed = new FormattedText(Query.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Query.FontFamily, Query.FontStyle, Query.FontWeight, Query.FontStretch), Query.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double left = typed.WidthIncludingTrailingWhitespace + 12; // + the text box's own padding and a gap
        AskHint.Margin = new Thickness(left, 0, 0, 0);
        AskHint.Visibility = left + AskHint.ActualWidth < Query.ActualWidth ? Visibility.Visible : Visibility.Collapsed;
    }

    static string BusyLabel(string tool) => tool.Split(' ', 2)[0] switch
    {
        "screenshot" or "ocr_screen" or "find_text" => "화면 보는 중",
        _ => "처리하는 중",
    };

    void OpenAiPanel()
    {
        if (_compact) { _compact = false; AnimateWidth(null); }
        if (AiPanel.Visibility == Visibility.Visible) return;
        // The conversation grows open above the box (width and height together), then its text fades in.
        AiPanel.Visibility = Visibility.Visible;
        AiPanel.Measure(new Size(FullWidth, double.PositiveInfinity));
        var grow = Anim(0, AiPanel.DesiredSize.Height, MoveMs, Settle);
        grow.Completed += (_, _) => { AiPanel.BeginAnimation(HeightProperty, null); AiPanel.Height = double.NaN; };
        AiPanel.Height = AiPanel.DesiredSize.Height;
        AiPanel.BeginAnimation(HeightProperty, grow);
        AiPanel.Opacity = 0;
        var show = Anim(0, 1, 420, Settle);
        show.BeginTime = TimeSpan.FromMilliseconds(140);
        AiPanel.BeginAnimation(OpacityProperty, show);
        UpdateShape();
    }

    void AiKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var text = Query.Text.Trim();
                if (text.Length > 0) // also while busy: the AI may be waiting for "진행" or "취소"
                {
                    Query.Clear();
                    AddTurn(text);
                    _ai.FollowUp(text);
                }
                break;
            case Key.Escape:
                if (Query.Text.Length > 0) Query.Clear();
                else ExitAi();
                break;
            case Key.Up or Key.PageUp: AiScroll.ScrollToVerticalOffset(AiScroll.VerticalOffset - 80); break;
            case Key.Down or Key.PageDown: AiScroll.ScrollToVerticalOffset(AiScroll.VerticalOffset + 80); break;
            default: return;
        }
        e.Handled = true;
    }

    void AddTurn(string question)
    {
        // The question as a chat bubble on the right, the answer as plain text on the left, as in Siri.
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 10, 16, 11),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 540,
            Margin = new Thickness(0, AiLog.Children.Count == 0 ? 0 : 20, 0, 12),
            Child = new TextBlock { Text = question, FontSize = 15.5, TextWrapping = TextWrapping.Wrap },
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "GlyphBackBrush");
        AiLog.Children.Add(bubble);
        _aiAnswer = new TextBlock { FontSize = 19, TextWrapping = TextWrapping.Wrap, LineHeight = 29, Visibility = Visibility.Collapsed };
        AiLog.Children.Add(_aiAnswer);
        _aiBusy = true;
        SetAiBusy("생각하는 중");
        AiScroll.ScrollToEnd();
        UpdateShape();
    }

    void OnAiLine(AiLineKind kind, string text)
    {
        if (!_aiMode || _aiAnswer is null) return;
        if (kind == AiLineKind.Tool)
        {
            if (_aiBusy) SetAiBusy(BusyLabel(text));
            return;
        }
        // Collected while thinking; shown when the answer is complete.
        _aiAnswer.Text += (_aiAnswer.Text.Length > 0 ? "\n" : "") + text;
        _aiAnswer.Visibility = Visibility.Visible;
    }

    void OnAiDone()
    {
        _aiBusy = false;
        SetAiBusy(null);
        if (!_aiMode) return;
        OpenAiPanel();
        AiScroll.ScrollToEnd();
        UpdateShape();
    }

    void ExitAi()
    {
        _aiMode = false;
        _aiBusy = false;
        _compact = false;
        SetAiBusy(null);
        Shell.BeginAnimation(WidthProperty, null);
        Shell.Width = double.NaN;
        AiPanel.BeginAnimation(HeightProperty, null);
        AiPanel.Height = double.NaN;
        AiPanel.Visibility = Visibility.Collapsed;
        AiLog.Children.Clear();
        _aiAnswer = null;
        Placeholder.Text = "묻거나 검색";
        Query.Clear();
        Render([], keepSelection: false); // back to the bare search bar
    }

    bool IsGrid => AppGrid.Visibility == Visibility.Visible;
    ListBox ActiveList => IsGrid ? AppGrid : Results;
    ResultItem? Selected => ActiveList.SelectedItem as ResultItem;

    /// <summary>Creates the HWND (needed for the hotkey) without showing the window.</summary>
    public void InitializeHidden()
    {
        Handle = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(Handle).CompositionTarget.BackgroundColor = Colors.Transparent;
        NativeUi.MakeGpuTransparent(Handle);
        ApplyBackdrop();
    }

    /// <summary>"acrylic" = blurred snapshot of what is behind the window; "solid" = opaque panel.</summary>
    public void ApplyBackdrop()
    {
        bool solid = _settings.Backdrop.Equals("solid", StringComparison.OrdinalIgnoreCase);
        Backdrop.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;
        Tint.SetResourceReference(Border.BackgroundProperty, solid ? "SolidPanelBrush" : "PanelBrush");
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
        var (work, scale, hz) = NativeUi.MonitorUnderCursor();
        _frameRate = Math.Clamp(hz, 30, 500);
        int windowWidthPx = (int)(Width * scale);
        int x = work.Left + (work.Width - windowWidthPx) / 2;
        int panelTop = work.Top + Math.Max(0, (int)((work.Height - MaxPanelHeight * scale) / 2));
        int margin = (int)(ShadowMargin * scale);
        int y = panelTop - margin;

        if (!IsVisible) _previousForeground = NativeUi.Foreground();

        // Snapshot the screen under the panel before the window covers it (skipped when still fading out: it would capture itself).
        if (!IsVisible && Backdrop.Visibility == Visibility.Visible)
            CaptureBackdrop(x + margin, panelTop, (int)((Width - 2 * ShadowMargin) * scale), (int)(MaxPanelHeight * scale), scale);

        // Start invisible: otherwise the panel flashes fully opaque until the animation's first frame.
        Shell.BeginAnimation(OpacityProperty, null);
        Shell.Opacity = 0;
        _hiding = false; // shown again mid fade-out: removing the fade above means its Completed never fires
        NativeUi.Move(Handle, x, y);
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
        // Like Spotlight: the previous query stays, selected, so typing replaces it.
        Query.SelectAll();
        // Wait for the first frame (layout, backdrop upload, bitmap cache) so the slow start is not eaten by the
        // time-based animation, which would otherwise jump ahead.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (IsVisible && !_hiding) AnimateIn(); });
        _ = UpdateStatusAsync();
        _ = _engine.WarmUpAsync();
    }

    public void HideLauncher()
    {
        if (!IsVisible || _hiding) return;
        _updateCts?.Cancel(); // closing the launcher cancels an update check or download
        _cts?.Cancel(); // no point finishing Everything queries nobody will see
        Disarm();
        CancelHold();
        ClosePreview();
        if (ActiveList.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        AnimateOut(() => { Hide(); if (_aiMode) ExitAi(); });
    }

    void CaptureBackdrop(int x, int y, int width, int height, double scale)
    {
        int pad = (int)(BackdropPad * scale);
        try
        {
            Backdrop.Source = ScreenCapture.CaptureBlurred(x - pad, y - pad, width + 2 * pad, height + 2 * pad, scale, BackdropBlur);
            _backdropLeft = -pad / scale;
            Canvas.SetLeft(Backdrop, _backdropLeft);
            Canvas.SetTop(Backdrop, -pad / scale);
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
    /// Text is hinted for motion meanwhile: pixel-snapped glyphs (the placeholder, the search icon) wobble while the
    /// scale changes.
    /// </summary>
    void CacheBody(bool on)
    {
        Body.CacheMode = on ? new BitmapCache() : null;
        TextOptions.SetTextHintingMode(Shell, on ? TextHintingMode.Animated : TextHintingMode.Auto);
    }

    // Fade in while the content comes into focus (blur → sharp) and the panel settles from a slightly smaller scale.
    // The blur is on the content only: blurring the panel's edge too made it look like it shrank and grew again.
    void AnimateIn()
    {
        _hiding = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        const double ms = 230;
        CacheBody(true);
        var blur = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
        Body.Effect = blur;
        Shell.BeginAnimation(OpacityProperty, Anim(0, 1, 170, ease));
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.97, 1, ms, ease));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.97, 1, ms, ease));
        var focus = Anim(16, 0, ms, ease);
        // Drop the effect and the cache once sharp: an idle BlurEffect would still cost a render pass per frame.
        focus.Completed += (_, _) =>
        {
            if (Body.Effect != blur) return; // hiding again already
            Body.Effect = null;
            CacheBody(false);
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, focus);
    }

    // The same motion in reverse: out of focus, fade out, then hide.
    void AnimateOut(Action done)
    {
        _hiding = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        const double ms = 160;
        CacheBody(true);
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        Body.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, Anim(0, 16, ms, ease));
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 0.97, ms, ease));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 0.97, ms, ease));
        var fade = Anim(null, 0, ms, ease);
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return; // shown again mid-animation
            _hiding = false;
            done();
        };
        Shell.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- shape ----------

    void Panel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateShape();

    /// <summary>A pill while only the search bar shows, rounded corners once the panel expands.</summary>
    void UpdateShape()
    {
        var size = Panel.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        // A narrowed (centered) panel shows the part of the screen snapshot that is really behind it.
        Canvas.SetLeft(Backdrop, _backdropLeft - (FullWidth - size.Width) / 2);
        bool lists = Results.Visibility == Visibility.Visible || IsGrid || _previewOpen;
        Separator.Visibility = lists ? Visibility.Visible : Visibility.Collapsed;
        bool expanded = lists || AiPanel.Visibility == Visibility.Visible || ActivityPanel.Visibility == Visibility.Visible;
        double target = expanded ? PanelRadius : SearchRow.Height / 2;
        if (target != _radiusTarget)
        {
            // The corners ease between pill and panel instead of snapping when the first result appears.
            _radiusTarget = target;
            BeginAnimation(ShapeRadiusProperty, Anim(null, target, 300, Settle));
        }
        ApplyShape();
    }

    double _radiusTarget = double.NaN;

    public static readonly DependencyProperty ShapeRadiusProperty = DependencyProperty.Register(
        nameof(ShapeRadius), typeof(double), typeof(MainWindow), new PropertyMetadata(44.0, (d, _) => ((MainWindow)d).ApplyShape()));

    /// <summary>Corner radius of the panel, animated by <see cref="UpdateShape"/>.</summary>
    public double ShapeRadius
    {
        get => (double)GetValue(ShapeRadiusProperty);
        set => SetValue(ShapeRadiusProperty, value);
    }

    void ApplyShape()
    {
        var size = Panel.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        double r = Math.Min(ShapeRadius, size.Height / 2);
        Panel.Clip = new RectangleGeometry(new Rect(size), r, r);
        ShadowShape.CornerRadius = Outline.CornerRadius = AiGlowLine.CornerRadius = new CornerRadius(r);
    }

    // ---------- searching ----------

    void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_aiMode) return; // the box is typing a follow-up, not a search
        Disarm();
        ClosePreview();
        if (AppCatalog.IsCatalogQuery(Query.Text))
        {
            _cts?.Cancel();
            _lastQuery = Query.Text.Trim();
            ShowCatalog();
            return;
        }
        if (IsGrid) AppGrid.Visibility = Visibility.Collapsed;
        _ = RunSearchAsync(Query.Text);
    }

    async Task RunSearchAsync(string text, bool expandSystemFolders = false, bool expandSystemFiles = false)
    {
        // Safe to dispose right after Cancel: the previous search only ever checks an already-cancelled token.
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        _lastQuery = text.Trim();
        if (_lastQuery.Length == 0)
        {
            Render([], keepSelection: false);
            return;
        }
        try
        {
            // Instant providers first; files slot in as Everything answers (fast name-prefix pass, then the full pass).
            var fast = await _engine.SearchAsync(text, ct: token, files: FileStage.None,
                expandSystemFolders: expandSystemFolders, expandSystemFiles: expandSystemFiles);
            if (token.IsCancellationRequested) return;
            Render(fast, keepSelection: false);
            // Later passes bring files; skip them when file search is off.
            if (_lastQuery.Length < 2 || !_settings.FileSearch) return;

            await Task.Delay(60, token); // debounce the Everything round-trips while typing
            IReadOnlyList<SearchResult> results = fast;
            foreach (var stage in new[] { FileStage.Prefix, FileStage.Full })
            {
                results = await _engine.SearchAsync(text, ct: token, files: stage,
                    expandSystemFolders: expandSystemFolders, expandSystemFiles: expandSystemFiles);
                if (token.IsCancellationRequested) return;
                Render(results, keepSelection: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("search failed", ex); }
    }

    void Render(IReadOnlyList<SearchResult> results, bool keepSelection)
    {
        // Later file stages keep whatever is selected, including the top hit, so Enter opens what the user saw.
        string? selectedKey = keepSelection && Results.SelectedItem is ResultItem sel ? sel.Result.Key : null;

        // Top hit first, then one section per kind in order of each kind's best result.
        var ordered = new List<ResultItem>();
        if (results.Count > 0) ordered.Add(new ResultItem(results[0], TopHitGroup));
        foreach (var group in results.Skip(1).GroupBy(r => ResultItem.KindLabel(r.Kind)))
            ordered.AddRange(group.Select(r => new ResultItem(r, group.Key)));

        _items.Clear();
        int generation = _icons.NextGeneration();
        foreach (var item in ordered)
        {
            _items.Add(item);
            if (item.Result.Key == _armedKey) item.SetSubtitleOverride(ArmedText);
            if (item.Result.Action == ActionType.Update && _updateStatus is not null) item.SetSubtitleOverride(_updateStatus);
            LoadIcon(item, generation);
        }

        // The armed command dropped out of the list: forget it, so it can never run on a single Enter later.
        if (_armedKey is not null && !ordered.Exists(i => i.Result.Key == _armedKey)) _armedKey = null;

        if (ordered.Exists(i => i.Result.Action == ActionType.Update)) _ = CheckForUpdateAsync();
        if (ordered.Exists(i => i.Result.Action == ActionType.AskAi)) _ai.WarmUp(); // a question is coming: start the assistant now
        UpdateAskHint(ordered.Count > 0 && ordered[0].Result.Action == ActionType.AskAi);

        bool any = _items.Count > 0;
        Separator.Visibility = Results.Visibility = Footer.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (_previewOpen) Results.Visibility = Visibility.Collapsed; // a later file stage while the preview is open
        UpdateShape();
        if (!any) return;
        int index = selectedKey is null ? 0 : Math.Max(0, ordered.FindIndex(i => i.Result.Key == selectedKey));
        Select(index, disarm: false);
    }

    void LoadIcon(ResultItem item, int generation)
    {
        if (item.Result.Kind == ResultKind.Clipboard && _clipboard.Thumbnail(item.Result.Target) is { } thumb) { item.Icon = thumb; return; }
        if (item.Icon is not null || item.Result.IconSource is not { } src) return;
        bool isFolder = item.Result.Kind == ResultKind.Folder || item.Result.Kind == ResultKind.Path && src.EndsWith('\\');
        _icons.Load(src, isFolder, generation, img =>
        {
            if (Dispatcher.CheckAccess()) item.Icon = img;
            else Dispatcher.BeginInvoke(() => item.Icon = img);
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
            _apps.Clear();
            foreach (var section in AppCatalog.Build(source))
                foreach (var app in section.Apps)
                    _apps.Add(new ResultItem(new SearchResult
                    {
                        Title = app.Name,
                        Subtitle = "애플리케이션",
                        Kind = ResultKind.App,
                        Target = app.LaunchTarget,
                        IconSource = app.LaunchTarget,
                        RevealPath = app.FilePath,
                    }, section.Name));
        }
        int generation = _icons.NextGeneration();
        foreach (var item in _apps) LoadIcon(item, generation);

        _items.Clear();
        Results.Visibility = Visibility.Collapsed;
        AppGrid.Visibility = Separator.Visibility = Footer.Visibility = Visibility.Visible;
        UpdateShape();
        if (_apps.Count == 0) { FooterText.Text = "앱 목록을 읽는 중입니다…"; return; }
        AppGrid.SelectedIndex = 0;
        AppGrid.ScrollIntoView(AppGrid.SelectedItem);
        UpdateFooter();
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
        // Alt+. : Everything's own settings (indexed folders and drives, exclusions). With Alt held WPF reports Key.System.
        if (e.Key == Key.System && e.SystemKey == Key.OemPeriod)
        {
            _ = OpenEverythingOptionsAsync();
            e.Handled = true;
            return;
        }
        if (_aiMode) { AiKeyDown(e); return; }
        if (_previewOpen)
        {
            if (e.Key == Key.Space && e.IsRepeat) { e.Handled = true; return; } // still holding the Space that opened it
            if (e.Key is Key.Escape or Key.Space) { ClosePreview(); e.Handled = true; return; }
            if (e.Key is Key.Up or Key.Down)
            {
                Select(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1));
                if (Selected is { } next && CanPreview(next.Result)) OpenPreview();
                else ClosePreview();
                e.Handled = true;
                return;
            }
        }
        // Holding Space on a file or folder opens its preview; a tap still types a space.
        if (e.Key == Key.Space && mods == ModifierKeys.None && !IsGrid && Selected is { } held && CanPreview(held.Result))
        {
            if (!e.IsRepeat && !_holding) StartHold(held);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Down when IsGrid: MoveInGrid(+1); e.Handled = true; break;
            case Key.Up when IsGrid: MoveInGrid(-1); e.Handled = true; break;
            case Key.Right when IsGrid: SelectIn(AppGrid, AppGrid.SelectedIndex + 1); e.Handled = true; break;
            case Key.Left when IsGrid: SelectIn(AppGrid, AppGrid.SelectedIndex - 1); e.Handled = true; break;
            case Key.Down: Select(Results.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: Select(Results.SelectedIndex - 1); e.Handled = true; break;
            case Key.PageDown: SelectIn(ActiveList, ActiveList.SelectedIndex + (IsGrid ? 21 : 5)); e.Handled = true; break;
            case Key.PageUp: SelectIn(ActiveList, ActiveList.SelectedIndex - (IsGrid ? 21 : 5)); e.Handled = true; break;
            case Key.Enter when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                if (Selected is { } toElevate && ShellLauncher.CanRunAsAdmin(toElevate.Result.RevealPath ?? toElevate.Result.Target))
                    RunAsAdmin(toElevate, toElevate.Result.RevealPath ?? toElevate.Result.Target);
                e.Handled = true; break;
            case Key.System when e.SystemKey == Key.Enter: // Alt+Enter
                if (Selected is { } toInspect) ShowProperties(toInspect);
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
                if (_updateBusy && _updateCts is { IsCancellationRequested: false } updating) updating.Cancel(); // "Esc로 취소"
                else if (_armedKey is not null) Disarm();
                else if (Query.Text.Length > 0) Query.Clear();
                else HideLauncher();
                e.Handled = true; break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                if (Selected is { } toCopy) CopyPath(toCopy);
                e.Handled = true; break;
        }
    }

    void Query_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_holding) return;
        e.Handled = true;
        FinishHold();
    }

    /// <summary>Ends a Space hold: a tap (preview not opened yet) types the space.</summary>
    void FinishHold()
    {
        bool tapped = _holdTimer.IsEnabled;
        CancelHold();
        if (!tapped) return; // the preview opened
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
        if (HitItem(list, e).Item is not { } item) return;
        e.Handled = true;
        list.SelectedItem = item;
        UpdateFooter();
        if (e.ClickCount >= 2) Execute(item);
        Query.Focus();
    }

    void List_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var (container, item) = HitItem(list, e);
        if (container is null || item is null) return;
        e.Handled = true;
        list.SelectedItem = item;
        UpdateFooter();
        var menu = BuildMenu(item);
        menu.PlacementTarget = container;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        list.ContextMenu = menu;
        menu.Closed += (_, _) => Query.Focus();
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
        return menu;
    }

    void Select(int index, bool disarm = true) => SelectIn(Results, index, disarm);

    void SelectIn(ListBox list, int index, bool disarm = true)
    {
        if (list.Items.Count == 0) return;
        index = Math.Clamp(index, 0, list.Items.Count - 1);
        if (disarm && list.SelectedIndex != index) Disarm();
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
        UpdateFooter();
    }

    // ---------- actions ----------

    void Execute(ResultItem item)
    {
        var r = item.Result;
        if (r.Action == ActionType.None) return; // informational row
        if (r.Action == ActionType.Update) { _ = RunUpdateAsync(); return; }
        if (r.Action == ActionType.AskAi) { StartAi(r.Target); return; }
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

    void ShowProperties(ResultItem item)
    {
        if (item.Result.RevealPath is not { } path) return;
        HideLauncher();
        if (!NativeUi.ShowProperties(path)) Log.Info("no properties for " + path);
    }

    async Task ShowAndSearchExpandedAsync(string query, bool folders, bool files)
    {
        ShowLauncher();
        await RunSearchAsync(query, folders, files);
    }

    void RunAsAdmin(ResultItem item, string path)
    {
        HideLauncher();
        _engine.RecordSelection(_lastQuery, item.Result);
        try { ShellLauncher.RunAsAdmin(path); }
        catch (Exception ex) { ShowError("관리자 권한으로 실행하지 못했습니다", path, ex); }
    }

    void Reveal(ResultItem item)
    {
        if (item.Result.RevealPath is not { } path) return;
        HideLauncher();
        _engine.RecordSelection(_lastQuery, item.Result);
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
        foreach (var i in _items) if (i.Result.Key == _armedKey) i.SetSubtitleOverride(null);
        _armedKey = null;
    }

    void UpdateFooter()
    {
        if (_previewOpen) { FooterText.Text = "Space / Esc  닫기      ↑↓  다른 항목 미리보기"; return; }
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
            ActionType.AskAi => "AI에게 묻기",
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
        if (r.RevealPath is not null) parts.Add("Alt+↵  속성      우클릭  더 보기");
        parts.Add("Esc  닫기");
        FooterText.Text = string.Join("      ", parts);
    }

    // ---------- preview (hold Space) ----------

    const double PreviewHoldMs = 1000;
    const double PullDelayMs = 150; // a tap (typing a space) should not make the row twitch
    readonly System.Windows.Threading.DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewHoldMs) };
    bool _holding;
    ListBoxItem? _pulledRow;
    bool _previewOpen;
    int _previewGeneration;

    static bool CanPreview(SearchResult r) => r.RevealPath is not null && r.Kind is ResultKind.File or ResultKind.Folder or ResultKind.Path;

    /// <summary>Space went down on a previewable row: start the timer and stretch the row as if it were being pulled.</summary>
    void StartHold(ResultItem item)
    {
        _holding = true;
        _holdTimer.Start();
        if (Results.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row) return;
        _pulledRow = row;
        var scale = new ScaleTransform();
        row.RenderTransformOrigin = new Point(0.5, 0.5);
        row.RenderTransform = scale;
        // The row swells the longer Space is held, as if about to pop open into the preview.
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        AnimationTimeline Pull(double to)
        {
            var a = Anim(null, to, PreviewHoldMs - PullDelayMs, ease);
            a.BeginTime = TimeSpan.FromMilliseconds(PullDelayMs);
            return a;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Pull(1.04));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Pull(1.12));
    }

    void CancelHold()
    {
        _holding = false;
        _holdTimer.Stop();
        SpringBack();
    }

    /// <summary>Lets go of the pulled row: it snaps back and wobbles like a released spring.</summary>
    void SpringBack()
    {
        if (_pulledRow?.RenderTransform is not ScaleTransform scale) return;
        _pulledRow = null;
        var ease = new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 1, 550, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 1, 550, ease));
    }

    void OpenPreview()
    {
        if (Selected is not { } item || item.Result.RevealPath is not { } path) return;
        bool wasOpen = _previewOpen;
        _previewOpen = true;
        int generation = ++_previewGeneration;
        PreviewTitle.Text = item.Title;
        PreviewInfo.Text = path;
        PreviewImage.Source = null;
        PreviewText.Text = "";
        Results.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;
        UpdateShape();
        UpdateFooter();
        if (!wasOpen)
        {
            // Pops out with a little overshoot: the spring that was being pulled.
            var pop = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
            PreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.9, 1, 340, pop));
            PreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.9, 1, 340, pop));
            PreviewPanel.BeginAnimation(OpacityProperty, Anim(0, 1, 180, new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        _ = LoadPreviewAsync(path, generation);
    }

    void ClosePreview()
    {
        if (!_previewOpen) return;
        _previewOpen = false;
        _previewGeneration++;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewText.Text = "";
        Results.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateShape();
        UpdateFooter();
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
                else if (await RunSta(() => ShellIcons.Get(path, 512, thumbnail: true)) is { } px)
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
        PreviewInfo.Text = info;
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

    // ---------- background activity ----------

    /// <summary>Tracker events come from any thread, often in bursts (pip output): sync once per dispatcher turn.</summary>
    void QueueActivitySync()
    {
        if (Interlocked.Exchange(ref _activitySyncQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _activitySyncQueued, 0);
            SyncActivities();
        });
    }

    /// <summary>Mirrors the tracker into the bars, updating rows in place so progress animates instead of flickering.</summary>
    void SyncActivities()
    {
        var snapshot = ActivityTracker.Shared.Snapshot();
        for (int i = _activities.Count - 1; i >= 0; i--)
            if (!snapshot.Any(a => a.Id == _activities[i].Id)) _activities.RemoveAt(i);
        foreach (var info in snapshot)
        {
            var item = _activities.FirstOrDefault(a => a.Id == info.Id);
            if (item is null) _activities.Add(item = new ActivityItem(info.Id));
            item.Update(info);
        }
        var visibility = _activities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ActivityPanel.Visibility == visibility) return;
        ActivityPanel.Visibility = visibility;
        UpdateShape();
    }

    /// <summary>
    /// While the launcher is open, shows Everything's indexing as a bar: loading its database (after a restart or a
    /// rescan) right away, busy only when it lasts, since every search makes Everything briefly busy too.
    /// </summary>
    async Task PollIndexAsync()
    {
        var state = _settings.FileSearch ? await Task.Run(() => EverythingClient.GetDbState(200)) : EverythingClient.DbState.NotRunning;
        _indexBusyPolls = state == EverythingClient.DbState.Busy ? _indexBusyPolls + 1 : 0;
        string? detail = state switch
        {
            EverythingClient.DbState.Loading => "색인을 읽는 중",
            EverythingClient.DbState.Busy when _indexBusyPolls >= 3 => "변경 사항을 반영하는 중",
            _ => null,
        };
        if (detail is null)
        {
            _indexActivity?.Dispose();
            _indexActivity = null;
        }
        else if (_indexActivity is null) _indexActivity = ActivityTracker.Shared.Begin("everything", "파일 색인 중 (Everything)", null, detail);
        else _indexActivity.Report("파일 색인 중 (Everything)", null, detail);
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
    async Task UpdateStatusAsync()
    {
        const string noEverything = "Everything 응답 없음 · 파일 결과 제외";
        if (Status.Text == noEverything) Status.Text = "";
        if (!_settings.FileSearch) return;
        var version = await Task.Run(() => EverythingClient.GetVersion(300));
        // Only touch our own message, so an error shown meanwhile (e.g. a failed launch) stays visible.
        if (version is null && Status.Text.Length == 0) Status.Text = noEverything;
        else if (version is not null && Status.Text == noEverything) Status.Text = "";
    }
}

static class ListExtensions
{
    public static int FindIndex<T>(this List<T> list, Func<T, bool> pred) => list.FindIndex(new Predicate<T>(pred));
}
