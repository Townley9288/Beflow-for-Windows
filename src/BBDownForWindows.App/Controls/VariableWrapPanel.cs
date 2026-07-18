using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.Controls;

public sealed class VariableWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(VariableWrapPanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(VariableWrapPanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var availableWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        var rowWidth = 0d;
        var rowHeight = 0d;
        var desiredWidth = 0d;
        var desiredHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            var additionalWidth = rowWidth <= 0 ? size.Width : HorizontalSpacing + size.Width;
            if (rowWidth > 0 && rowWidth + additionalWidth > availableWidth)
            {
                desiredWidth = Math.Max(desiredWidth, rowWidth);
                desiredHeight += rowHeight + (desiredHeight > 0 ? VerticalSpacing : 0);
                rowWidth = size.Width;
                rowHeight = size.Height;
            }
            else
            {
                rowWidth += additionalWidth;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
        }

        desiredWidth = Math.Max(desiredWidth, rowWidth);
        if (rowHeight > 0) desiredHeight += rowHeight + (desiredHeight > 0 ? VerticalSpacing : 0);
        return new Windows.Foundation.Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : Math.Min(desiredWidth, availableSize.Width),
            desiredHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            var additionalWidth = x <= 0 ? size.Width : HorizontalSpacing + size.Width;
            if (x > 0 && x + additionalWidth > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }
            if (x > 0) x += HorizontalSpacing;
            child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }

    private static void OnLayoutPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is VariableWrapPanel panel) panel.InvalidateMeasure();
    }
}
