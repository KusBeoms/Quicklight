using System.Windows;
using System.Windows.Controls;

namespace Quicklight;

/// <summary>Large card for direct answers on top, a content card for clipboard entries, the normal row for everything else.</summary>
public sealed class ResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Row { get; set; }
    public DataTemplate? Hero { get; set; }
    public DataTemplate? Clip { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item switch
        {
            ResultItem { IsHero: true } => Hero,
            ResultItem { IsClip: true } => Clip,
            _ => Row,
        };
}
