using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class RenamePage : Page
{
    public RenamePage()
    {
        ViewModel = new RenameViewModel(((App)Application.Current).Services);
        NavigationCacheMode = NavigationCacheMode.Required;
        InitializeComponent();
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PageBackground_PointerPressed), true);
    }

    public RenameViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
        await ViewModel.InitializeAsync(e.Parameter as RenameNavigationContext);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow);
        if (!string.IsNullOrWhiteSpace(folder)) await ViewModel.LoadDirectoryAsync(folder);
    }

    private async void ReloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.DirectoryPath)) await ViewModel.LoadDirectoryAsync(ViewModel.DirectoryPath);
    }

    private void NumericTextBox_LostFocus(object sender, RoutedEventArgs e) => ViewModel.NormalizeNumericInputs();

    private void AdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        var expanded = AdvancedSettingsPanel.Visibility != Visibility.Visible;
        AdvancedSettingsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSettingsChevron.Glyph = expanded ? "\uE70D" : "\uE76C";
    }

    private void PageBackground_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;
        if (!FocusSink.Focus(FocusState.Programmatic)) SearchTmdbButton.Focus(FocusState.Programmatic);
    }

    private static bool IsInsideInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBox or NumberBox or ButtonBase or Selector or ToggleSwitch or ListViewItem or GridViewItem) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void SearchTmdb_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SearchTmdbAsync();
        if (ViewModel.TmdbResults.Count == 0) return;

        var resultList = new ListView
        {
            ItemsSource = ViewModel.TmdbResults,
            ItemTemplate = (DataTemplate)Resources["TmdbResultTemplate"],
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 430,
            MinWidth = 620
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择 TMDB 匹配结果",
            Content = resultList,
            PrimaryButtonText = "应用所选",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        resultList.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = resultList.SelectedItem is TmdbSearchResult;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && resultList.SelectedItem is TmdbSearchResult result)
        {
            ViewModel.SelectedTmdbResult = result;
            await ViewModel.ApplyTmdbResultAsync(result);
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await ViewModel.PreviewAsync();

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认执行重命名",
            Content = ViewModel.PreviewSummary + "。执行过程中如果任何一步失败，Beflow 会尝试回滚全部变更。",
            PrimaryButtonText = "执行重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ExecuteAsync();
    }

    private void ManageTemplates_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).MainWindow.Navigate("rename-templates", new RenameTemplatesNavigationContext(ViewModel.MediaType, ViewModel.SelectedTemplate?.Id));

    private void RenameHistory_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).MainWindow.Navigate("history", new HistoryNavigationContext(HistorySection.Renames));

    private async void UndoLatest_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedHistory = ViewModel.HistoryRecords.FirstOrDefault(record => record.UndoneAt is null);
        if (ViewModel.SelectedHistory is not null) await ConfirmUndoAsync();
    }

    private async Task ConfirmUndoAsync()
    {
        if (ViewModel.SelectedHistory is null) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "撤销重命名", Content = "Beflow 会先确认当前文件仍存在且原文件名未被占用，然后恢复这条记录中的全部文件名。", PrimaryButtonText = "撤销", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.UndoSelectedHistoryAsync();
    }

}
