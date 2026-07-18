using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class RenameTemplatesPage : Page
{
    private bool _syncingSelection;
    private readonly List<ToggleButton> _fieldButtons = [];
    private string? _selectedField;
    private double _fieldPaletteWidth;
    private TextBox? _activeDownloadNamingTextBox;

    public RenameTemplatesPage()
    {
        var services = ((App)Application.Current).Services;
        ViewModel = new RenameTemplatesViewModel(services);
        DownloadNamingViewModel = new DownloadNamingViewModel(services);
        InitializeComponent();
    }

    public RenameTemplatesViewModel ViewModel { get; }
    public DownloadNamingViewModel DownloadNamingViewModel { get; }
    public bool HasUnsavedChanges => ViewModel.HasUnsavedChanges || DownloadNamingViewModel.HasUnsavedChanges;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await DownloadNamingViewModel.InitializeAsync(e.Parameter as DownloadNamingNavigationContext);
        await ViewModel.InitializeAsync(e.Parameter as RenameTemplatesNavigationContext);
        RuleTabs.SelectedIndex = e.Parameter is RenameTemplatesNavigationContext ? 1 : 0;
        SyncSelectionControls();
        SyncDownloadNamingControls();
        RebuildFieldPalette(FieldPalette.ActualWidth, true);
        DispatcherQueue.TryEnqueue(() =>
        {
            _activeDownloadNamingTextBox = MainFolderTemplateBox;
            UpdateDownloadFieldButtons();
        });
    }

    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "放弃未保存的命名规则？",
            Content = "当前下载命名或影视重命名规则已经修改，但尚未保存。继续操作会丢弃这些修改。",
            PrimaryButtonText = "放弃修改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        ViewModel.DiscardChanges();
        DownloadNamingViewModel.DiscardChanges();
        SyncDownloadNamingControls();
        return true;
    }

    private void RuleTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleTabs.SelectedIndex == 1)
            DispatcherQueue.TryEnqueue(() => RebuildFieldPalette(FieldPalette.ActualWidth, true));
    }

    private void SingleProfile_Click(object sender, RoutedEventArgs e)
    {
        DownloadNamingViewModel.ChangeProfile(DownloadNamingProfileKind.SingleVideo);
        SyncDownloadNamingControls();
    }

    private void MultiProfile_Click(object sender, RoutedEventArgs e)
    {
        DownloadNamingViewModel.ChangeProfile(DownloadNamingProfileKind.MultiEpisode);
        SyncDownloadNamingControls();
    }

    private void DownloadNamingTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _activeDownloadNamingTextBox = sender as TextBox;
        UpdateDownloadFieldButtons();
    }

    private void DownloadNamingField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fieldName }) return;
        var target = _activeDownloadNamingTextBox ?? FileNameTemplateBox;
        var section = GetSection(target);
        var field = DownloadNamingViewModel.FieldGroups.SelectMany(group => group.Fields)
            .FirstOrDefault(item => item.Name == fieldName);
        if (field is null || !field.Sections.HasFlag(section)) return;

        var start = Math.Clamp(target.SelectionStart, 0, target.Text.Length);
        var length = Math.Clamp(target.SelectionLength, 0, target.Text.Length - start);
        target.Text = target.Text.Remove(start, length).Insert(start, field.Token);
        target.Focus(FocusState.Programmatic);
        target.SelectionStart = start + field.Token.Length;
        target.SelectionLength = 0;
    }

    private void DownloadNamingField_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _activeDownloadNamingTextBox is null) return;
        UpdateDownloadFieldButton(button, GetSection(_activeDownloadNamingTextBox));
    }

    private async void SaveDownloadNaming_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DownloadNamingViewModel.SaveAsync();
            SyncDownloadNamingControls();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DownloadNamingViewModel.ReportError(exception);
        }
    }

    private void RestoreDownloadNamingDefault_Click(object sender, RoutedEventArgs e)
    {
        DownloadNamingViewModel.RestoreCurrentDefault();
        SyncDownloadNamingControls();
    }

    private async void MediaTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || MediaTypeBox.SelectedIndex < 0) return;
        var requested = MediaTypeBox.SelectedIndex == 1 ? RenameMediaType.Movie : RenameMediaType.Series;
        if (requested == ViewModel.MediaType) return;
        if (!await ConfirmDiscardChangesAsync())
        {
            SyncSelectionControls();
            return;
        }
        ViewModel.ChangeMediaType(requested);
        SyncSelectionControls();
    }

    private async void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || TemplateList.SelectedItem is not RenameTemplateListItem requested || ReferenceEquals(requested, ViewModel.SelectedItem)) return;
        if (!await ConfirmDiscardChangesAsync())
        {
            SyncSelectionControls();
            return;
        }
        ViewModel.SelectTemplate(requested);
        SyncSelectionControls();
    }

    private void AddLayoutField_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedField is not null) ViewModel.AddTemplateField(_selectedField);
    }

    private void FieldPalette_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildFieldPalette(e.NewSize.Width);

    private void RebuildFieldPalette(double availableWidth, bool force = false)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return;
        if (!force && Math.Abs(availableWidth - _fieldPaletteWidth) < 1) return;
        _fieldPaletteWidth = availableWidth;

        FieldPalette.Children.Clear();
        FieldPalette.RowDefinitions.Clear();
        _fieldButtons.Clear();

        const double preferredFieldWidth = 104;
        const double spacing = 8;
        var fields = ViewModel.FieldOptions;
        var maximumColumns = Math.Clamp((int)Math.Floor((availableWidth + spacing) / (preferredFieldWidth + spacing)), 1, fields.Count);
        var rowCount = (int)Math.Ceiling(fields.Count / (double)maximumColumns);
        var fieldsPerRow = fields.Count / rowCount;
        var extraFields = fields.Count % rowCount;
        var fieldIndex = 0;

        for (var row = 0; row < rowCount; row++)
        {
            FieldPalette.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowGrid = new Grid { ColumnSpacing = spacing };
            Grid.SetRow(rowGrid, row);
            FieldPalette.Children.Add(rowGrid);

            var count = fieldsPerRow + (row < extraFields ? 1 : 0);
            for (var column = 0; column < count; column++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var field = fields[fieldIndex++];
                var button = new ToggleButton
                {
                    Content = field,
                    Tag = field,
                    IsChecked = string.Equals(_selectedField, field, StringComparison.Ordinal),
                    IsEnabled = ViewModel.HasSelectedTemplate,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    MinHeight = 40,
                    Padding = new Thickness(8, 6, 8, 6)
                };
                button.Click += FieldOption_Click;
                Grid.SetColumn(button, column);
                rowGrid.Children.Add(button);
                _fieldButtons.Add(button);
            }
        }
    }

    private void FieldOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string field } selected) return;
        _selectedField = field;
        foreach (var button in _fieldButtons) button.IsChecked = ReferenceEquals(button, selected);
    }

    private async void CreateTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        var name = await RequestTextAsync("新建模板", "模板名称", string.Empty);
        if (name is not null) await RunActionAsync(() => ViewModel.CreateTemplateAsync(name));
    }

    private void MoveFieldUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string field }) ViewModel.MoveTemplateField(field, -1);
    }

    private void MoveFieldDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string field }) ViewModel.MoveTemplateField(field, 1);
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string field }) ViewModel.RemoveTemplateField(field);
    }

    private void ApplyFieldLayout_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyFieldLayout();
    private void ReadTemplateFields_Click(object sender, RoutedEventArgs e) => ViewModel.ReadFieldsFromPattern();

    private async void SaveChanges_Click(object sender, RoutedEventArgs e) => await RunActionAsync(ViewModel.SaveChangesAsync);

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var name = await RequestTextAsync("另存为并应用", "新模板名称", string.Empty);
        if (name is not null) await RunActionAsync(() => ViewModel.SaveAsAndApplyAsync(name));
    }

    private async void RenameTemplate_Click(object sender, RoutedEventArgs e)
    {
        var name = await RequestTextAsync("重命名模板", "新的模板名称", ViewModel.SelectedTemplate?.Name ?? string.Empty);
        if (name is not null) await RunActionAsync(() => ViewModel.RenameSelectedAsync(name));
    }

    private async void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTemplate is not { BuiltIn: false } template) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除模板",
            Content = $"确定删除“{template.Name}”吗？如果它正在使用，将自动恢复为对应的内置模板。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunActionAsync(ViewModel.DeleteSelectedAsync);
    }

    private async void SetActive_Click(object sender, RoutedEventArgs e) => await RunActionAsync(ViewModel.SetActiveAsync);

    private async void BackToRename_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        ((App)Application.Current).MainWindow.Navigate("rename");
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            SyncSelectionControls();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ViewModel.ReportError(exception);
        }
    }

    private async Task<string?> RequestTextAsync(string title, string label, string initial)
    {
        var box = new TextBox { Header = label, Text = initial, MinWidth = 360, SelectionStart = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private void SyncSelectionControls()
    {
        _syncingSelection = true;
        MediaTypeBox.SelectedIndex = ViewModel.MediaType == RenameMediaType.Movie ? 1 : 0;
        TemplateList.SelectedItem = ViewModel.SelectedItem;
        _syncingSelection = false;
    }

    private void SyncDownloadNamingControls()
    {
        SingleProfileButton.IsChecked = DownloadNamingViewModel.ProfileKind == DownloadNamingProfileKind.SingleVideo;
        MultiProfileButton.IsChecked = DownloadNamingViewModel.ProfileKind == DownloadNamingProfileKind.MultiEpisode;
        UpdateDownloadFieldButtons();
    }

    private void UpdateDownloadFieldButtons()
    {
        if (_activeDownloadNamingTextBox is null) return;
        var section = GetSection(_activeDownloadNamingTextBox);
        var supported = DownloadNamingViewModel.FieldGroups.SelectMany(group => group.Fields)
            .ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (var button in Descendants(RuleTabs).OfType<Button>())
        {
            if (button.Tag is string name && supported.ContainsKey(name)) UpdateDownloadFieldButton(button, section);
        }
    }

    private void UpdateDownloadFieldButton(Button button, DownloadNamingFieldSections section)
    {
        if (button.Tag is not string name) return;
        var field = DownloadNamingViewModel.FieldGroups.SelectMany(group => group.Fields)
            .FirstOrDefault(item => item.Name == name);
        if (field is null) return;
        button.IsEnabled = field.Sections.HasFlag(section);
        ToolTipService.SetToolTip(button, button.IsEnabled ? $"插入 {field.Token}" : "当前名称组件不支持此字段");
    }

    private DownloadNamingFieldSections GetSection(TextBox textBox) =>
        ReferenceEquals(textBox, MainFolderTemplateBox) ? DownloadNamingFieldSections.MainFolder :
        ReferenceEquals(textBox, SubfolderTemplateBox) ? DownloadNamingFieldSections.Subfolder :
        DownloadNamingFieldSections.FileName;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
