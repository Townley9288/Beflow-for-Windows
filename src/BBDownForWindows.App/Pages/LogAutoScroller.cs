using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BBDownForWindows.App.Pages;

internal sealed class LogAutoScroller
{
    private const double BottomTolerance = 24;
    private readonly TextBox _textBox;
    private ScrollViewer? _scrollViewer;
    private bool _followLatest = true;

    public LogAutoScroller(TextBox textBox)
    {
        _textBox = textBox;
        _textBox.Loaded += TextBox_Loaded;
        _textBox.Unloaded += TextBox_Unloaded;
        _textBox.TextChanged += TextBox_TextChanged;
    }

    public void FollowLatest()
    {
        _followLatest = true;
        QueueScrollToEnd();
    }

    private void TextBox_Loaded(object sender, RoutedEventArgs e)
    {
        AttachScrollViewer();
        QueueScrollToEnd();
    }

    private void TextBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is not null) _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
        _scrollViewer = null;
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_followLatest) QueueScrollToEnd();
    }

    private void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scrollViewer is null) return;
        _followLatest = _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset <= BottomTolerance;
    }

    private void QueueScrollToEnd()
    {
        _textBox.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            AttachScrollViewer();
            _scrollViewer?.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
        });
    }

    private void AttachScrollViewer()
    {
        if (_scrollViewer is not null) return;
        _scrollViewer = FindDescendant<ScrollViewer>(_textBox);
        if (_scrollViewer is not null) _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
