using BBDownForWindows.Core;

namespace BBDownForWindows.App;

public sealed class AppServices
{
    public AppServices(ApplicationPaths paths)
    {
        Paths = paths;
        HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        UpdateHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        Settings = new SettingsStore(paths);
        History = new HistoryStore(paths);
        UpdateState = new UpdateStateStore(paths);
        ProcessRunner = new ProcessRunner();
        ToolLocator = new ToolLocator(paths);
        TaskManager = new TaskManager(paths, ProcessRunner);
        TaskConsole = new ViewModels.TaskConsoleViewModel(TaskManager);
        BBDown = new BBDownService(paths, ProcessRunner, ToolLocator, Settings);
        DualAudio = new DualAudioService(paths, BBDown, ProcessRunner, Settings, ToolLocator);
        AccountStatus = new AccountStatusService(paths, HttpClient);
        Updates = new UpdateService(UpdateHttpClient);
        UpdateCoordinator = new UpdateCoordinator(this);
    }

    public ApplicationPaths Paths { get; }
    public HttpClient HttpClient { get; }
    public HttpClient UpdateHttpClient { get; }
    public ISettingsStore Settings { get; }
    public IHistoryStore History { get; }
    public IUpdateStateStore UpdateState { get; }
    public IProcessRunner ProcessRunner { get; }
    public IToolLocator ToolLocator { get; }
    public ITaskManager TaskManager { get; }
    public ViewModels.TaskConsoleViewModel TaskConsole { get; }
    public IBBDownService BBDown { get; }
    public IDualAudioService DualAudio { get; }
    public IAccountStatusService AccountStatus { get; }
    public IUpdateService Updates { get; }
    public UpdateCoordinator UpdateCoordinator { get; }
}
