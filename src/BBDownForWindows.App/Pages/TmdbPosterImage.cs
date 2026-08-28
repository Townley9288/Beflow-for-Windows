using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BBDownForWindows.App.Pages;

public static class TmdbPosterImage
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(TmdbPosterImage),
        new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Image image) return;

        image.Source = args.NewValue is string value && !string.IsNullOrWhiteSpace(value)
            ? new BitmapImage(new Uri(value, UriKind.Absolute))
            : null;
    }
}
