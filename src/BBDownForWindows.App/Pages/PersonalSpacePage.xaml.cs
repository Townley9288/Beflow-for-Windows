using BBDownForWindows.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed partial class PersonalSpacePage : Page
{
    private bool ready;
    public PersonalSpaceViewModel ViewModel { get; }
    public PersonalSpacePage()
    {
        ViewModel = ((App)Application.Current).Services.PersonalSpace;
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        ready = true;
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        if (e.Parameter is DownloadInputNavigationContext input) await ViewModel.ReceiveInputAsync(input.Input, input.ParseAutomatically);
    }
    private async void Uploads_Checked(object sender, RoutedEventArgs e) { if (ready) await ViewModel.SwitchTabAsync(0); }
    private async void Collections_Checked(object sender, RoutedEventArgs e) { if (ready) await ViewModel.SwitchTabAsync(1); }
    private async void Collection_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpaceCollectionViewModel item && ViewModel.OpenCollectionCommand.CanExecute(item)) await ViewModel.OpenCollectionCommand.ExecuteAsync(item);
    }
    private async void User_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpaceUserViewModel user && ViewModel.OpenUserCommand.CanExecute(user))
            await ViewModel.OpenUserCommand.ExecuteAsync(user);
    }
    private async void Input_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !ViewModel.ReadCommand.CanExecute(null)) return;
        e.Handled = true;
        await ViewModel.ReadCommand.ExecuteAsync(null);
    }
    private void Cover_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var cover = (FrameworkElement)sender;
        var height = e.NewSize.Width * 9 / 16;
        if (Math.Abs(cover.Height - height) > 0.1) cover.Height = height;
    }
    private void Input_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = ((App)Application.Current).MainWindow.DragLinkMonitoringEnabled && BilibiliDataTransfer.MayContainInput(e.DataView) && ViewModel.IsIdle
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }
    private async void Input_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!((App)Application.Current).MainWindow.DragLinkMonitoringEnabled || ViewModel.IsBusy) return;
        try
        {
            var inputs = await BilibiliDataTransfer.ExtractInputsAsync(e.DataView);
            if (inputs.Count > 0) await ViewModel.ReceiveInputAsync(inputs[0], true);
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "无法读取拖入的链接", Content = ex.Message, CloseButtonText = "关闭" }.ShowAsync();
        }
    }
    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow);
        if (!string.IsNullOrWhiteSpace(folder)) ViewModel.WorkDirectory = folder;
    }
    private void Queue_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).MainWindow.Navigate("queue");
    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).MainWindow.Navigate("settings");
    private void Naming_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).MainWindow.Navigate("rename-templates");
}
