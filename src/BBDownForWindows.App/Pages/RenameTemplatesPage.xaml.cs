using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class RenameTemplatesPage : Page
{
    private bool _syncingSelection;
    private readonly List<ToggleButton> _fieldButtons = [];
    private string? _selectedField;
    private double _fieldPaletteWidth;

    public RenameTemplatesPage()
    {
        ViewModel = new RenameTemplatesViewModel(((App)Application.Current).Services);
        InitializeComponent();
    }

    public RenameTemplatesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync(e.Parameter as RenameTemplatesNavigationContext);
        SyncSelectionControls();
        RebuildFieldPalette(FieldPalette.ActualWidth, true);
    }

    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!ViewModel.HasUnsavedChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "放弃未保存的模板修改？",
            Content = "当前模板规则已经修改，但尚未保存。继续操作会丢弃这些修改。",
            PrimaryButtonText = "放弃修改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        ViewModel.DiscardChanges();
        return true;
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
}
