using System.Collections.ObjectModel;
using System.Diagnostics;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class DownloadQueuePage : Page
{
    private readonly AppServices services = ((App)Application.Current).Services;
    private readonly ObservableCollection<QueueRow> rows = [];
    private readonly object progressGate = new();
    private QueueProgress? pendingProgress;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool active;
    public DownloadQueuePage()
    {
        InitializeComponent(); QueueList.ItemsSource = rows;
        timer.Tick += (_, _) =>
        {
            QueueProgress? update;
            lock (progressGate) { update = pendingProgress; pendingProgress = null; }
            if (update is not null) rows.FirstOrDefault(r => r.Item.Id == update.Id)?.Progress(update);
        };
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        active = true; services.DownloadQueue.Changed += Changed; services.DownloadQueue.ProgressChanged += Progress;
        timer.Start(); await Run(services.DownloadQueue.InitializeAsync); Refresh(); base.OnNavigatedTo(e);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false; services.DownloadQueue.Changed -= Changed; services.DownloadQueue.ProgressChanged -= Progress;
        timer.Stop(); base.OnNavigatedFrom(e);
    }
    private void Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (active) Refresh(); });
    private void Progress(object? sender, QueueProgress e) { lock (progressGate) pendingProgress = e; }
    private void Refresh()
    {
        var queue = services.DownloadQueue.Snapshot;
        for (var index = rows.Count - 1; index >= 0; index--) if (!queue.Items.Any(i => i.Id == rows[index].Item.Id)) rows.RemoveAt(index);
        for (var index = 0; index < queue.Items.Count; index++)
        {
            var item = queue.Items[index]; var row = rows.FirstOrDefault(r => r.Item.Id == item.Id);
            if (row is null) { row = new QueueRow(item); rows.Insert(index, row); }
            else { row.Update(item); var previous = rows.IndexOf(row); if (previous != index) rows.Move(previous, index); }
        }
        var pausing = queue.Items.Any(i => i.State == DownloadQueueState.Pausing);
        QueueStatus.Text = pausing ? "正在停止当前进程并保存状态…" : queue.Paused ? "已暂停 · 点击继续后执行" : $"共 {queue.Items.Count} 个任务 · 按顺序执行";
        PauseButton.Content = queue.Paused ? "继续" : "暂停"; PauseButton.IsEnabled = !pausing && services.DownloadQueue.Error.Length == 0;
        if (services.DownloadQueue.Error.Length > 0) { ErrorBar.Message = services.DownloadQueue.Error; ErrorBar.IsOpen = true; }
    }
    private static DownloadQueueItem Item(object sender) => ((QueueRow)((FrameworkElement)sender).DataContext).Item;
    private async Task Run(Func<Task> action)
    { try { await action(); } catch (Exception exception) { ErrorBar.Message = exception.Message; ErrorBar.IsOpen = true; } }
    private async void Pause_Click(object sender, RoutedEventArgs e) => await Run(() => services.DownloadQueue.Snapshot.Paused ? services.DownloadQueue.ResumeAsync() : services.DownloadQueue.PauseAsync());
    private async void Clear_Click(object sender, RoutedEventArgs e) => await Run(services.DownloadQueue.ClearCompletedAsync);
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Run(services.DownloadQueue.CancelCurrentAsync);
    private async void Up_Click(object sender, RoutedEventArgs e) => await Run(() => services.DownloadQueue.MoveAsync(Item(sender).Id, -1));
    private async void Down_Click(object sender, RoutedEventArgs e) => await Run(() => services.DownloadQueue.MoveAsync(Item(sender).Id, 1));
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Run(() => services.DownloadQueue.RemoveAsync(Item(sender).Id));
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Run(() => services.DownloadQueue.RetryFailedAsync(Item(sender).Id));
    private async void Edit_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var item = await services.DownloadQueue.BeginEditAsync(Item(sender).Id);
        ((App)Application.Current).MainWindow.Navigate(item.Kind == DownloadQueueKind.Download ? "download" : "dual", new QueueNavigationContext(item, true));
    });
    private async void EditFailures_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var item = services.DownloadQueue.CreateRetry(Item(sender).Id);
        ((App)Application.Current).MainWindow.Navigate(item.Kind == DownloadQueueKind.Download ? "download" : "dual", new QueueNavigationContext(item, false));
        return Task.CompletedTask;
    });
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Item(sender).OutputDirectory;
        if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
    private async void Logs_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var list = new ListView { MaxHeight = 400, SelectionMode = ListViewSelectionMode.Single, ItemsSource = Item(sender).LogPaths };
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is string path && File.Exists(path))
            { var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(path); Process.Start(start); }
        };
        list.IsItemClickEnabled = true;
        await new ContentDialog { XamlRoot = XamlRoot, Title = "任务日志（点击打开）", Content = list, CloseButtonText = "关闭" }.ShowAsync();
    });
    private async void Details_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var item = Item(sender);
        var content = new StackPanel { Spacing = 8, MaxWidth = 720 };
        content.Children.Add(new TextBlock { Text = $"任务 ID：{item.Id}\n添加时间：{item.AddedAt:yyyy-MM-dd HH:mm:ss}\n{item.Url}\n{item.DualAudio?.SourceBUrl}\n{item.OutputDirectory}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var entries = item.Checkpoint.Units.Select(u => $"P{u.Number} {u.Title}\n{(u.Completed ? "完成" : u.Error.Length > 0 ? "失败：" + u.Error : "未完成")}\nA：{u.SourceA.Stage}  B：{u.SourceB.Stage}\n{u.FinalPath}").ToList();
        content.Children.Add(new ListView { ItemsSource = entries, MaxHeight = 400, SelectionMode = ListViewSelectionMode.None });
        await new ContentDialog { XamlRoot = XamlRoot, Title = item.Title, Content = content, CloseButtonText = "关闭" }.ShowAsync();
    });
}

public sealed class QueueRow(DownloadQueueItem item) : ObservableObject
{
    public DownloadQueueItem Item { get; private set; } = item;
    public string Title => Item.Title;
    public string Links => Item.Url + (Item.DualAudio is null ? "" : "  /  " + Item.DualAudio.SourceBUrl);
    public string OutputDirectory => Item.OutputDirectory;
    public string Summary => $"{(Item.Kind == DownloadQueueKind.Download ? "普通下载" : "多音轨封装")} · 成功 {Item.Succeeded} / 失败 {Item.Failed} / 未完成 {Math.Max(0, Item.Total - Item.Succeeded - Item.Failed)}";
    public string Error => Item.Error;
    public string StateText => Item.State switch
    {
        DownloadQueueState.Waiting => "等待", DownloadQueueState.Editing => "编辑中", DownloadQueueState.Running => "运行中",
        DownloadQueueState.Pausing => "正在停止", DownloadQueueState.Paused => "已暂停", DownloadQueueState.Completed => "完成",
        DownloadQueueState.PartialFailure => "部分失败", DownloadQueueState.Failed => "失败", DownloadQueueState.Cancelled => "已取消", _ => ""
    };
    public Visibility WaitingVisibility => Item.CanEdit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => Item.State == DownloadQueueState.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => Item.State is DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Editing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RetryVisibility => Item.IsTerminal && (Item.Failed > 0 || Item.State == DownloadQueueState.Failed) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Item.State is DownloadQueueState.Running or DownloadQueueState.Pausing ? Visibility.Visible : Visibility.Collapsed;
    public double Percent { get; private set; }
    public bool Indeterminate { get; private set; }
    public string ProgressText { get; private set; } = "";
    public void Update(DownloadQueueItem value) { Item = value; OnPropertyChanged(string.Empty); }
    public void Progress(QueueProgress progress)
    {
        Percent = progress.Percent ?? 0; Indeterminate = progress.Percent is null;
        ProgressText = $"P{progress.Current} · {progress.Phase} · {progress.Speed} {progress.Eta}";
        OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(Indeterminate)); OnPropertyChanged(nameof(ProgressText));
    }
}
