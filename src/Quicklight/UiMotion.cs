using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Quicklight;

internal static class UiMotion
{
    public const int Quick = 120, Standard = 180, Expand = 280;
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static void Fade(UIElement element, double to, int duration = Standard, Action? completed = null)
    {
        double from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = to;
        if (!Enabled || from == to)
        {
            completed?.Invoke();
            return;
        }

        var animation = Ease(from, to, duration);
        if (completed is not null) animation.Completed += (_, _) => completed();
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    public static void Enter(UIElement element, double offset = 5, int duration = Standard)
    {
        if (!Enabled) return;
        element.Opacity = 0;
        Fade(element, 1, duration);
        var translate = new TranslateTransform();
        element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, Ease(offset, 0, duration));
    }

    static DoubleAnimation Ease(double from, double to, int duration) => new(from, to, TimeSpan.FromMilliseconds(duration))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop,
    };
}
