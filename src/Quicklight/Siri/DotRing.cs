using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Quicklight.Siri;

/// <summary>
/// Eight dots rotate at a steady speed. The brush follows the host's theme.
/// </summary>
public sealed class DotRing : FrameworkElement
{
    const int Dots = 8;
    const double Design = 26, Radius = 9.2, DotSize = 4.2, Period = 1350;

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(DotRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush DotBrush { get => (Brush)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }

    readonly RotateTransform _spin = new();
    bool _spinning = true;

    public bool Spinning
    {
        get => _spinning;
        set { if (_spinning != value) { _spinning = value; Sync(); } }
    }

    public DotRing()
    {
        Width = Height = Design;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _spin;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Sync();
    }

    void Sync()
    {
        double angle = _spin.Angle % 360;
        if (IsVisible && Spinning && UiMotion.Enabled)
            _spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(angle, angle + 360, TimeSpan.FromMilliseconds(Period)) { RepeatBehavior = RepeatBehavior.Forever });
        else
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin.Angle = angle;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double k = ActualWidth / Design, c = ActualWidth / 2;
        for (int i = 0; i < Dots; i++)
        {
            double a = i * Math.PI / 4;
            dc.PushOpacity(0.24 + 0.76 * i / (Dots - 1));
            dc.DrawEllipse(DotBrush, null, new Point(c + Radius * k * Math.Cos(a), c + Radius * k * Math.Sin(a)), DotSize * k / 2, DotSize * k / 2);
            dc.Pop();
        }
    }
}
