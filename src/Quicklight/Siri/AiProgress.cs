using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Quicklight.Core.Ai;

namespace Quicklight.Siri;

internal sealed class AiProgress : StackPanel
{
    readonly DotRing _ring;
    readonly TextBlock[] _labels;
    int _active;

    public AiProgress(double diameter, double fontSize, string foreground)
    {
        Orientation = Orientation.Horizontal;
        IsHitTestVisible = false;
        _ring = new DotRing { Width = diameter, Height = diameter, VerticalAlignment = VerticalAlignment.Center };
        _ring.SetResourceReference(DotRing.DotBrushProperty, foreground);
        Children.Add(_ring);

        var labels = new Grid { Margin = new Thickness(diameter < 24 ? 8 : diameter > 40 ? 16 : 10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _labels = new[] { "생각하는 중", "찾아보는 중", "확인 대기 중", "처리 대기 중" }.Select((text, i) =>
        {
            var label = new TextBlock { Text = text, FontSize = fontSize, Visibility = i == 0 ? Visibility.Visible : Visibility.Hidden };
            label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
            labels.Children.Add(label);
            return label;
        }).ToArray();
        Children.Add(labels);
    }

    public void SetState(AiState state, bool awaitingConfirmation = false, bool animate = true)
    {
        int index = awaitingConfirmation ? 2 : state == AiState.Loading ? 3 : state == AiState.Searching ? 1 : 0;
        _ring.Spinning = !awaitingConfirmation;
        if (index == _active && animate) return;
        _active = index;
        AutomationProperties.SetName(this, _labels[index].Text);

        // Hidden labels still reserve their width. A new state can reverse an in-flight fade.
        for (int i = 0; i < _labels.Length; i++)
        {
            var label = _labels[i];
            bool active = i == index;
            if (!animate || !IsVisible)
            {
                label.BeginAnimation(OpacityProperty, null);
                label.Opacity = active ? 1 : 0;
                label.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            }
            else if (active)
            {
                if (label.Visibility == Visibility.Hidden) label.Opacity = 0;
                label.Visibility = Visibility.Visible;
                UiMotion.Fade(label, 1);
            }
            else if (label.Visibility == Visibility.Visible)
                UiMotion.Fade(label, 0, UiMotion.Quick, () =>
                {
                    if (label != _labels[_active]) label.Visibility = Visibility.Hidden;
                });
        }
    }
}
