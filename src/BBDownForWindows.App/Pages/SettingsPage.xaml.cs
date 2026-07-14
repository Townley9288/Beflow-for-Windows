using BBDownForWindows.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() { ViewModel = new SettingsViewModel(((App)Application.Current).Services); InitializeComponent(); }
    public SettingsViewModel ViewModel { get; }
    protected override async void OnNavigatedTo(NavigationEventArgs e) { await ViewModel.InitializeAsync(); base.OnNavigatedTo(e); }
    private async void BrowseWorkDir_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.Settings.WorkDirectory = value; }
    private async void BrowseAria_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.Settings.Aria2cPath = value; }
    private async void BrowseMkv_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.Settings.MkvmergePath = value; }
    private async void Apply_Click(object sender, RoutedEventArgs e) { await ViewModel.SaveAsync(); ((App)Application.Current).MainWindow.Navigate("download"); }
}
