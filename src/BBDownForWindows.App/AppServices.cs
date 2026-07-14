using BBDownForWindows.Core;

namespace BBDownForWindows.App;

public sealed class AppServices
{
    public AppServices(ApplicationPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(paths);
        History = new HistoryStore(paths);
        ProcessRunner = new ProcessRunner();
        ToolLocator = new ToolLocator(paths);
        TaskManager = new TaskManager(paths, ProcessRunner);
        TaskConsole = new ViewModels.TaskConsoleViewModel(TaskManager);
        BBDown = new BBDownService(paths, ProcessRunner, ToolLocator, Settings);
        DualAudio = new DualAudioService(paths, BBDown, ProcessRunner, Settings, ToolLocator);
    }

    public ApplicationPaths Paths { get; }
    public ISettingsStore Settings { get; }
    public IHistoryStore History { get; }
    public IProcessRunner ProcessRunner { get; }
    public IToolLocator ToolLocator { get; }
    public ITaskManager TaskManager { get; }
    public ViewModels.TaskConsoleViewModel TaskConsole { get; }
    public IBBDownService BBDown { get; }
    public IDualAudioService DualAudio { get; }
}
