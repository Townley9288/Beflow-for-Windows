using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BBDownForWindows.App.Controls;

// Space directories display at most 30 items per page. Measure every card so
// the tallest card sets a shared height without clipping titles or status messages.
public sealed class AdaptiveCardPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveCardPanel),
        new PropertyMetadata(220d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveCardPanel),
        new PropertyMetadata(12d, OnLayoutPropertyChanged));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth : Math.Max(0, availableSize.Width);
        var (columns, itemWidth) = GetColumns(width);
        var items = Children.Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        var itemHeight = 0d;
        foreach (var item in items)
        {
            item.Measure(new Size(itemWidth, double.PositiveInfinity));
            itemHeight = Math.Max(itemHeight, item.DesiredSize.Height);
        }
        var rows = (items.Length + columns - 1) / columns;
        var height = rows * itemHeight + Math.Max(0, rows - 1) * Spacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = GetColumns(finalSize.Width);
        var items = Children.Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        var itemHeight = items.Select(item => item.DesiredSize.Height).DefaultIfEmpty(0).Max();
        for (var index = 0; index < items.Length; index++)
        {
            items[index].Arrange(new Rect(
                index % columns * (itemWidth + Spacing),
                index / columns * (itemHeight + Spacing), itemWidth, itemHeight));
        }
        return finalSize;
    }

    private (int Columns, double ItemWidth) GetColumns(double width)
    {
        var columns = Math.Max(1, (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing)));
        return (columns, Math.Max(0, (width - (columns - 1) * Spacing) / columns));
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((AdaptiveCardPanel)sender).InvalidateMeasure();
}
