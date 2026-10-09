using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Quicklight;

/// <summary>
/// Overlay scroll position for a control whose own scroll bar is hidden: shows while the wheel scrolls it (1.4 s, then
/// fades), stays and thickens under the mouse, and can be dragged. Put it over the host in the same cell, then <see cref="Attach"/>.
/// </summary>
public sealed class ScrollThumb : Border
{
    readonly Border _bar = new() { Width = 4, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 2, 0), IsHitTestVisible = false };
    FrameworkElement? _host;
    ScrollViewer? _scroll;
    bool _dragging;
    double _dragStartY, _dragStartOffset;

    public ScrollThumb()
    {
        Width = 14; // a wider grab area than the bar
        Height = 0;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.Transparent;
        Opacity = 0;
        _bar.SetResourceReference(BackgroundProperty, "SubtleTextBrush");
        Child = _bar;
    }

    /// <summary>The host contains the ScrollViewer (a ListBox, or a TextBox, through its template).</summary>
    public void Attach(FrameworkElement host)
    {
        _host = host;
        host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        host.PreviewMouseWheel += (_, _) => Flash();
        host.IsVisibleChanged += (_, _) => Sync();
        Sync();
    }

    void Sync()
    {
        Visibility = _host!.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv) return;
        _scroll = sv;
        double view = e.ViewportHeight, total = e.ExtentHeight, h = _host!.ActualHeight;
        if (total <= view + 1) { Height = 0; return; }
        double thumb = Math.Max(24, h * view / total);
        Height = thumb;
        Margin = new Thickness(0, (h - thumb) * e.VerticalOffset / (total - view), 0, 0);
    }

    void Flash()
    {
        if (IsMouseOver || _dragging) { Hold(); return; }
        FadeOut();
    }

    /// <summary>Fully visible for 1.4 s, then fades away.</summary>
    void FadeOut()
    {
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        BeginAnimation(OpacityProperty, fade);
    }

    void Hold()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    void Thicken(bool thick) =>
        _bar.BeginAnimation(WidthProperty, new DoubleAnimation(thick ? 10 : 4, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    /// <summary>Over the bar the host gets no wheel event, so scroll it from here (three 16 px lines per notch, like the host).</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_scroll is null) return;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * 48);
        Hold();
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Hold();
        Thicken(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        Thicken(false);
        FadeOut();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_scroll is null) return;
        _dragging = true;
        _dragStartY = e.GetPosition((IInputElement)Parent).Y;
        _dragStartOffset = _scroll.VerticalOffset;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _scroll is null) return;
        // The thumb travels (host height - thumb height); the content scrolls (extent - viewport) over that distance.
        double track = _host!.ActualHeight - Height;
        if (track <= 0) return;
        double dy = e.GetPosition((IInputElement)Parent).Y - _dragStartY;
        _scroll.ScrollToVerticalOffset(_dragStartOffset + dy * (_scroll.ExtentHeight - _scroll.ViewportHeight) / track);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (!IsMouseOver) { Thicken(false); FadeOut(); }
    }
}
