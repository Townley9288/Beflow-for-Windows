using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace BBDownForWindows.App;

public static class PickerHelper
{
    public static async Task<string?> PickFolderAsync(Window window)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickExecutableAsync(Window window, string suggestedExtension = ".exe")
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(suggestedExtension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
