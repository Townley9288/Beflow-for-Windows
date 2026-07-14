using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            var services = ((App)Application.Current).Services;
            var settings = await services.Settings.LoadAsync();
            var tools = services.ToolLocator.Locate(settings);
            var versions = await Task.WhenAll(
                services.ToolLocator.GetVersionAsync(tools.BBDown),
                services.ToolLocator.GetVersionAsync(tools.Aria2c),
                services.ToolLocator.GetVersionAsync(tools.Ffmpeg),
                services.ToolLocator.GetVersionAsync(tools.Mkvmerge));
            ToolVersions.Text = $"BBDown：{versions[0]}\naria2c：{versions[1]}\nFFmpeg：{versions[2]}\nmkvmerge：{versions[3]}";
        }
        catch (Exception exception)
        {
            ToolVersions.Text = $"工具检测失败：{exception.Message}";
        }
    }
}
