using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Quicklight.Siri;

/// <summary>
/// Six dots chasing each other round a ring. Every dot runs the same eased lap (slow off the mark, fast through the
/// middle, slow home), each a little later than the one before: they gather into a tight arc, fly apart into an even
/// ring and gather again, like a comet that keeps catching its own tail. The head is the biggest and brightest.
/// Drawn frame by frame (no timeline), so the lap stays in phase across state changes. The brush follows the host's theme.
/// </summary>
public sealed class DotRing : FrameworkElement
{
    const int Dots = 6;
    // Design units: a 60 wide ring. Everything scales with the element's size.
    const double Design = 60, Orbit = 22.5, HeadSize = 10.5;
    const double Period = 1700, Lag = 85, Fan = 0.5; // ms per lap, ms between dots, radians the resting arc fans out

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

    static double Ease(double u) => u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2; // in-out cubic

    protected override void OnRender(DrawingContext dc)
    {
        double k = ActualWidth / Design, c = ActualWidth / 2;
        double t = Animating ? Clock.Elapsed.TotalMilliseconds : 0;
        // Draw the tail first so the head ends up on top where they overlap.
        for (int i = Dots - 1; i >= 0; i--)
        {
            double angle;
            if (Animating)
            {
                double u = ((t - i * Lag) % Period + Period) % Period / Period;
                angle = -Math.PI / 2 + 2 * Math.PI * Ease(u) - i * Fan;
            }
            else angle = -Math.PI / 2 - i * (2 * Math.PI / Dots); // still: an even ring, fading behind the head
            double r = HeadSize * (1 - 0.07 * i) * k / 2;
            dc.PushOpacity(1 - 0.13 * i);
            dc.DrawEllipse(DotBrush, null, new Point(c + Orbit * k * Math.Cos(angle), c + Orbit * k * Math.Sin(angle)), r, r);
            dc.Pop();
        }
    }
}
