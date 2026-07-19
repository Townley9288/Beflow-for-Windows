using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.Controls;

public sealed partial class RenameHistoryDetailContent : UserControl
{
    public RenameHistoryDetailContent(RenameHistoryRecord record)
    {
        ViewModel = new RenameHistoryDetailViewModel(record);
        InitializeComponent();
    }

    public RenameHistoryDetailViewModel ViewModel { get; }
}
