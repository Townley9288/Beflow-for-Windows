using BBDownForWindows.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App;

public sealed class ThemeManager(ISettingsStore settingsStore)
{
    private FrameworkElement? _root;
    private AppWindow? _appWindow;

    public AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CurrentMode = (await settingsStore.LoadAsync(cancellationToken)).ThemeMode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CurrentMode = AppThemeMode.System;
        }
    }

    public void Attach(FrameworkElement root, AppWindow appWindow)
    {
        _root = root;
        _appWindow = appWindow;
        ApplyCurrentMode();
    }

    public async Task SetModeAsync(AppThemeMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == CurrentMode) return;

        var settings = await settingsStore.LoadAsync(cancellationToken);
        settings.SchemaVersion = 2;
        settings.ThemeMode = mode;
        await settingsStore.SaveAsync(settings, cancellationToken);

        CurrentMode = mode;
        ApplyCurrentMode();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCurrentMode()
    {
        if (_root is not null)
        {
            _root.RequestedTheme = CurrentMode switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        if (_appWindow is not null)
        {
            _appWindow.TitleBar.PreferredTheme = CurrentMode switch
            {
                AppThemeMode.Light => TitleBarTheme.Light,
                AppThemeMode.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
        }
    }
}
