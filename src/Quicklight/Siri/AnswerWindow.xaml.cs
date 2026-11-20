using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Quicklight.Core.Ai;

namespace Quicklight.Siri;

/// <summary>
/// The reply to a question asked from the launcher, one capsule changing shape. It starts exactly where the launcher's
/// search capsule was (same place, same size, showing the question), gathers into a status pill (the turning ring and what
/// the AI is doing, filling it), and when the answer is ready grows, top edge fixed, into a draggable,
/// resizable panel with the conversation. It stays on the monitor the launcher was opened on. ↗ continues in the app
/// window; Esc stops the work, then closes.
/// </summary>
public partial class AnswerWindow : Window
{
    const double ShadowMargin = 28;
    const double PillHeight = 68, PillRadius = PillHeight / 2, PanelRadius = 28;
    const double DefaultWidth = 420, DefaultHeight = 600, MinPanelWidth = 320, MinPanelHeight = 300;
    static readonly KeySpline BoundsEase = new(0.25, 0.10, 0.25, 1);

    readonly AiHost _host;
    readonly AiProgress _progress = new(28, 18, "TextBrush");
    Conversation? _conv;
    IntPtr _returnTo;
    Rect _origin;               // the launcher's capsule on screen, physical pixels
    double _scale = 1;          // physical pixels per DIP on the monitor the launcher was on
    double _panelWidth = DefaultWidth, _panelHeight = DefaultHeight;
    enum Mode { Hidden, Intro, Pill, Panel }
    Mode _mode = Mode.Hidden;
    int _generation;            // a late timer or animation of an earlier question must not touch this one
    EventHandler? _frame;
    double _radius = PillRadius;

    public AnswerWindow(AiHost host)
    {
        _host = host;
        InitializeComponent();
        Theme.ApplyInk(Resources);
        Status.Child = _progress;
        foreach (var t in Grips.Children.OfType<Thumb>()) t.Background = Brushes.Transparent;
        Chat.Attach(host);
        Chat.ConversationStarted += c => _conv = c;
        host.Changed += OnHostChanged;
        SourceInitialized += (_, _) => MakeTransparent();
        Closed += (_, _) => StopMorph();
    }

    void MakeTransparent()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        NativeUi.MakeGpuTransparent(hwnd);
        SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); // the AI's screen tools must not read this panel back
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    IntPtr Hwnd => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Settles on the monitor that holds <paramref name="p"/> (physical pixels): its scale and work area.</summary>
    NativeUi.RECT UseMonitorAt(Point p)
    {
        var m = NativeUi.MonitorAt((int)p.X, (int)p.Y);
        _scale = m.Scale;
        return m.Work;
    }

    // ---------- start ----------

    /// <summary>
    /// A new question. <paramref name="origin"/> is the launcher's capsule on screen (physical pixels): this window opens
    /// on exactly that rectangle, then gathers into the pill.
    /// </summary>
    public void Begin(Conversation conv, string question, Rect origin, IntPtr returnTo, Brush? capsuleTint = null)
    {
        _conv = conv;
        _returnTo = returnTo;
        Chat.Show(conv);
        if (_mode == Mode.Panel && IsVisible)
        {
            // Already open: the new question simply continues in the panel, as its own conversation.
            Activate();
            Chat.FocusInput();
            return;
        }

        int gen = ResetSurface();
        _mode = Mode.Intro;
        _origin = origin;
        UseMonitorAt(new Point(origin.X + origin.Width / 2, origin.Y + origin.Height / 2));
        if (capsuleTint is not null) CapsuleBg.Background = capsuleTint;
        else CapsuleBg.SetResourceReference(Border.BackgroundProperty, "BarTintBrush");
        QuestionText.Text = question.ReplaceLineEndings(" ");
        Question.Height = origin.Height / _scale;
        ResetOpacity(Question, 1);

        // Same place, same size as the launcher's capsule.
        double m = ShadowMargin * _scale;
        var start = new Rect(origin.X - m, origin.Y - m, origin.Width + 2 * m, origin.Height + 2 * m);
        SetCornerRadius(origin.Height / _scale / 2);
        SetBounds(start);
        Show();
        Activate();
        NativeUi.ForceForeground(Hwnd);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (gen != _generation || _mode != Mode.Intro) return;
            _mode = Mode.Pill;
            OnHostChanged();
            if (_mode != Mode.Pill) return;

            _progress.SetState(_host.State, animate: false);
            Status.Visibility = Visibility.Visible;
            Morph(PillRect(), PillRadius, UiMotion.Expand);
            UiMotion.Fade(Question, 0, UiMotion.Quick, () =>
            {
                if (gen == _generation && _mode == Mode.Pill) UiMotion.Fade(Status, 1);
            });
        });
    }

    int ResetSurface()
    {
        StopMorph();
        ResetOpacity(Shell, 1);
        ResetOpacity(Panel, 0);
        ResetOpacity(CapsuleBg, 1);
        ResetOpacity(Status, 0);
        ResetOpacity(Question, 0);
        Panel.Visibility = Grips.Visibility = Status.Visibility = Visibility.Collapsed;
        Panel.IsHitTestVisible = false;
        return ++_generation;
    }

    /// <summary>The pill just fits the dots and the words: its top edge where the capsule's was, centered on it.</summary>
    Rect PillRect()
    {
        Status.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Status.DesiredSize.Width;
        double w = (width + 2 * ShadowMargin) * _scale, h = (PillHeight + 2 * ShadowMargin) * _scale;
        double cx = _origin.X + _origin.Width / 2;
        double top = _origin.Y + (_origin.Height - PillHeight * _scale) / 2 - ShadowMargin * _scale;
        return new Rect(cx - w / 2, top, w, h);
    }

    Rect PanelRect()
    {
        var work = UseMonitorAt(new Point(_origin.X + _origin.Width / 2, _origin.Y + _origin.Height / 2));
        double w = (_panelWidth + 2 * ShadowMargin) * _scale, h = (_panelHeight + 2 * ShadowMargin) * _scale;
        h = Math.Min(h, work.Height - 20 * _scale);
        double cx = _origin.X + _origin.Width / 2;
        // Stays on the launcher's monitor, with the top edge where the capsule's was unless that would run off the bottom.
        double x = Math.Clamp(cx - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        double top = _origin.Y + (_origin.Height - PillHeight * _scale) / 2 - ShadowMargin * _scale;
        double y = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// A conversation that starts with something chosen outside the launcher (files from Explorer's menu): the panel
    /// opens with it attached and the cursor in the input, near the top of the monitor the pointer is on.
    /// </summary>
    public void Compose(IEnumerable<string> paths, string? draft, IntPtr returnTo)
    {
        _returnTo = returnTo;
        _conv = null;
        Chat.Show(null);
        Chat.AddAttachments(paths);
        if (!string.IsNullOrEmpty(draft)) Chat.SetDraft(draft);
        if (_mode == Mode.Panel && IsVisible) { Activate(); Chat.FocusInput(); return; }
        ResetSurface();
        var (work, scale, _) = NativeUi.MonitorUnderCursor();
        _scale = scale;
        _origin = new Rect(work.Left + work.Width / 2.0, work.Top + work.Height * 0.16, 0, PillHeight * scale);
        _mode = Mode.Pill;
        Expand();
    }

    // ---------- state ----------

    void OnHostChanged()
    {
        if (_mode != Mode.Pill) return;
        if (!ReferenceEquals(_host.BusyConversation, _conv) || _host.PendingConfirmation is not null) Expand();
        else _progress.SetState(_host.State);
    }

    /// <summary>
    /// The cursor goes to the input as the panel opens. The window may have lost the foreground while the AI worked (the
    /// launcher hiding hands it to another app), so it takes it back, unless the user has gone to some other app meanwhile:
    /// then their typing is not interrupted.
    /// </summary>
    void FocusInputNow()
    {
        if (!IsActive)
        {
            var front = NativeUi.Foreground();
            if (front != IntPtr.Zero && front != Hwnd && front != _returnTo) return;
            Activate();
            NativeUi.ForceForeground(Hwnd);
        }
        // After the layout that made the panel visible: focusing a control that is not laid out yet does nothing.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (_mode == Mode.Panel && IsVisible) Chat.FocusInput();
        });
    }

    /// <summary>The answer is ready (or the question failed): the pill becomes the panel.</summary>
    void Expand()
    {
        if (_mode == Mode.Panel) return;
        int gen = _generation;
        _mode = Mode.Panel;
        var rect = PanelRect();

        void Reveal()
        {
            if (gen != _generation) return;
            Panel.Visibility = Grips.Visibility = Visibility.Visible;
            Panel.IsHitTestVisible = true;
            UiMotion.Fade(Panel, 1);
            FocusInputNow();
        }

        if (!IsVisible)
        {
            ResetOpacity(CapsuleBg, 0);
            SetCornerRadius(PanelRadius);
            SetBounds(rect);
            ResetOpacity(Shell, 0);
            Show();
            Activate();
            NativeUi.ForceForeground(Hwnd);
            UiMotion.Fade(Shell, 1);
            Reveal();
            return;
        }
        UiMotion.Fade(Question, 0, UiMotion.Quick);
        UiMotion.Fade(Status, 0, UiMotion.Standard, () => Status.Visibility = Visibility.Collapsed);
        UiMotion.Fade(CapsuleBg, 0, UiMotion.Expand);
        Morph(rect, PanelRadius, UiMotion.Expand, Reveal);
    }

    // ---------- motion ----------

    void SetCornerRadius(double r)
    {
        _radius = r;
        var cr = new CornerRadius(r);
        CapsuleBg.CornerRadius = PanelBg.CornerRadius = cr;
        Panel.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth - 2 * ShadowMargin), Math.Max(0, ActualHeight - 2 * ShadowMargin)), r, r);
    }

    static void ResetOpacity(UIElement element, double opacity)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = opacity;
    }

    void SetBounds(Rect rect) => NativeUi.SetBounds(Hwnd, (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
        (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    void StopMorph()
    {
        if (_frame is null) return;
        CompositionTarget.Rendering -= _frame;
        _frame = null;
    }

    /// <summary>Moves the window to <paramref name="to"/> frame by frame: the window changes size, no content is scaled.</summary>
    void Morph(Rect to, double radius, int ms, Action? done = null)
    {
        StopMorph();
        var r0 = NativeUi.Bounds(Hwnd);
        var from = new Rect(r0.Left, r0.Top, r0.Width, r0.Height);
        double radius0 = _radius;
        if (!UiMotion.Enabled || !IsVisible || (from == to && radius0 == radius))
        {
            SetBounds(to);
            SetCornerRadius(radius);
            done?.Invoke();
            return;
        }
        TimeSpan? started = null;
        TimeSpan? lastFrame = null;
        _frame = (_, args) =>
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            if (now == lastFrame) return;
            lastFrame = now;
            started ??= now;
            double t = Math.Clamp((now - started.Value).TotalMilliseconds / ms, 0, 1);
            double e = BoundsEase.GetSplineProgress(t);
            double L(double a, double b) => a + (b - a) * e;
            SetBounds(new Rect(L(from.X, to.X), L(from.Y, to.Y), L(from.Width, to.Width), L(from.Height, to.Height)));
            SetCornerRadius(L(radius0, radius));
            if (t < 1) return;
            StopMorph();
            done?.Invoke();
        };
        CompositionTarget.Rendering += _frame;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_mode == Mode.Panel && Grips.Visibility == Visibility.Visible)
        {
            _panelWidth = Math.Max(MinPanelWidth, ActualWidth - 2 * ShadowMargin);
            _panelHeight = Math.Max(MinPanelHeight, ActualHeight - 2 * ShadowMargin);
        }
        SetCornerRadius(_radius);
    }

    // ---------- user input ----------

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // Dragging keeps the grabbed point under the pointer and moves the whole window, with no easing.
        DragMove();
    }

    void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var tag = (string)((Thumb)sender).Tag;
        var r = NativeUi.Bounds(Hwnd);
        double x = r.Left, y = r.Top, w = r.Width, h = r.Height;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX; // the window may have been dragged to another monitor
        double dx = e.HorizontalChange * scale, dy = e.VerticalChange * scale;
        double minW = (MinPanelWidth + 2 * ShadowMargin) * scale, minH = (MinPanelHeight + 2 * ShadowMargin) * scale;
        if (tag.Contains('R')) w = Math.Max(minW, w + dx);
        if (tag.Contains('B')) h = Math.Max(minH, h + dy);
        if (tag.Contains('L')) { double nw = Math.Max(minW, w - dx); x += w - nw; w = nw; } // the opposite side stays
        NativeUi.SetBounds(Hwnd, (int)x, (int)y, (int)w, (int)h);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.F4) { e.Handled = true; CloseAndReturn(); return; } // closing the real window would break the host's reference
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (OwnsBusy) _host.Cancel(); // Esc stops the work first, a second one closes
        else CloseAndReturn();
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseAndReturn();

    /// <summary>The AI is busy with the conversation shown here (or with one this panel just started).</summary>
    bool OwnsBusy => _host.IsBusy && (_conv is null || ReferenceEquals(_host.BusyConversation, _conv));

    void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (_conv is { } c && _host.Store.MessagesOf(c).Count > 0) _host.Expand(c);
    }

    // ---------- closing ----------

    /// <summary>Closes (100 ms), gives focus back to the app the question came from, and drops anything still being answered.</summary>
    public void CloseAndReturn()
    {
        if (OwnsBusy) _host.Cancel(); // work going on in the app window is not ours to stop
        CloseQuietly();
        if (_returnTo != IntPtr.Zero) NativeUi.ForceForeground(_returnTo);
    }

    /// <summary>Hides without returning focus (the conversation moved on to the app window).</summary>
    public void CloseQuietly()
    {
        if (_mode == Mode.Hidden) return;
        _generation++;
        int gen = _generation;
        _mode = Mode.Hidden;
        StopMorph();
        Panel.IsHitTestVisible = false;
        Grips.Visibility = Visibility.Collapsed;
        UiMotion.Fade(Shell, 0, UiMotion.Quick, () =>
        {
            if (gen != _generation) return; // opened again while fading
            Hide();
        });
    }
}
