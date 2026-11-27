using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Quicklight.Siri;

/// <summary>
/// Six dots round a ring. While it spins they play DotMotion's scenes in turn (a wave round a breathing ring, a globe
/// turn, a ripple); while it is still they rest as an even ring, fading behind the head at the top.
/// Drawn frame by frame (no timeline), so the motion stays in phase across state changes. The brush follows the host's theme.
/// </summary>
public sealed class DotRing : FrameworkElement
{
    const int Dots = DotMotion.Dots;
    // Design units: a 60 wide ring. Everything scales with the element's size.
    const double Design = 60;

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(DotRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush DotBrush { get => (Brush)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }

    static readonly Stopwatch Clock = Stopwatch.StartNew(); // shared: two rings on screen turn together
    bool _spinning = true, _attached;

    public bool Spinning
    {
        get => _spinning;
        set { if (_spinning != value) { _spinning = value; Sync(); } }
    }

    public DotRing()
    {
        Width = Height = Design;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Sync();
        Unloaded += (_, _) => Detach();
    }

    bool Animating => IsVisible && Spinning && UiMotion.Enabled;

    void Sync()
    {
        if (Animating && !_attached)
        {
            CompositionTarget.Rendering += OnFrame;
            _attached = true;
        }
        else if (!Animating) Detach();
        InvalidateVisual();
    }

    void Detach()
    {
        if (!_attached) return;
        CompositionTarget.Rendering -= OnFrame;
        _attached = false;
    }

    void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double k = ActualWidth / Design, c = ActualWidth / 2;
        bool animating = Animating;
        double t = animating ? Clock.Elapsed.TotalMilliseconds : 0;
        // Draw the tail first so the head ends up on top where they overlap.
        for (int i = Dots - 1; i >= 0; i--)
        {
            var p = animating
                ? DotMotion.At(i, t)
                // still: an even ring, fading behind the head
                : new DotMotion.Pose(-Math.PI / 2 - i * (2 * Math.PI / Dots), DotMotion.Orbit, DotMotion.HeadSize * (1 - 0.07 * i), 1 - 0.13 * i);
            double r = p.Size * k / 2;
            dc.PushOpacity(Math.Clamp(p.Opacity, 0, 1));
            dc.DrawEllipse(DotBrush, null, new Point(c + p.Radius * k * Math.Cos(p.Angle), c + p.Radius * k * Math.Sin(p.Angle)), r, r);
            dc.Pop();
        }
    }
}
