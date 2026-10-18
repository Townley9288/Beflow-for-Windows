using BBDownForWindows.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.Controls;

/// <summary>Uses normal ComboBox items with non-selectable source headings.</summary>
public sealed class StreamQualityComboBox : ComboBox
{
    public StreamQualityComboBox() => DefaultStyleKey = typeof(ComboBox);

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container)
        {
            var selectable = item is not DownloadEpisodeViewModel.Choice choice || choice.IsSelectable;
            container.IsEnabled = selectable;
            container.IsTabStop = selectable;
            container.FontSize = selectable ? FontSize : 12;
            container.MinHeight = selectable ? 36 : 28;
        }
    }
}
