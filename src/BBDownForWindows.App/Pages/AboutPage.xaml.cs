using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage() => InitializeComponent();
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var services = ((App)Application.Current).Services;
        var settings = await services.Settings.LoadAsync();
        var tools = services.ToolLocator.Locate(settings);
        ToolVersions.Text = $"{await services.ToolLocator.GetVersionAsync(tools.BBDown)}\n{await services.ToolLocator.GetVersionAsync(tools.Aria2c)}\n{await services.ToolLocator.GetVersionAsync(tools.Ffmpeg)}\n{await services.ToolLocator.GetVersionAsync(tools.Mkvmerge)}";
        base.OnNavigatedTo(e);
    }
}
