using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BBDownForWindows.Core;
using Microsoft.UI.Dispatching;

namespace BBDownForWindows.App.ViewModels;

public sealed class TaskConsoleViewModel : ObservableObject
{
    private const int MaximumCharacters = 240_000;
    private readonly ITaskManager _taskManager;
    private readonly DispatcherQueue? _dispatcher;
    private string _logs = string.Empty;
    private bool _isBusy;
    private string _status = "等待操作";

    public TaskConsoleViewModel(ITaskManager taskManager)
    {
        _taskManager = taskManager;
        try { _dispatcher = DispatcherQueue.GetForCurrentThread(); }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Headless unit tests do not bootstrap the Windows App SDK dispatcher.
        }
        CancelCommand = new AsyncRelayCommand(_taskManager.CancelActiveAsync, () => IsBusy);
        ClearCommand = new RelayCommand(() => Logs = string.Empty);
        taskManager.LogAppended += (_, text) => Dispatch(() => Append(text));
        taskManager.TaskChanged += (_, task) => Dispatch(() =>
        {
            IsBusy = task.State == TaskState.Running;
            Status = task.State switch
            {
                TaskState.Running => "任务运行中",
                TaskState.Completed => "任务已完成",
                TaskState.Cancelled => "任务已取消",
                TaskState.Failed => $"任务失败：{task.Error}",
                _ => "等待操作"
            };
        });
    }

    public string Logs { get => _logs; private set => SetProperty(ref _logs, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) CancelCommand.NotifyCanExecuteChanged();
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IAsyncRelayCommand CancelCommand { get; }
    public IRelayCommand ClearCommand { get; }

    private void Append(string text)
    {
        var combined = Logs + text;
        Logs = combined.Length > MaximumCharacters ? combined[^200_000..] : combined;
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess) action(); else _dispatcher.TryEnqueue(() => action());
    }
}
