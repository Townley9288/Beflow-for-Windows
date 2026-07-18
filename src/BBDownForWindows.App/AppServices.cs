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
        RenameSettings = new RenameSettingsStore(paths);
        RenameHistory = new RenameHistoryStore(paths);
        UpdateState = new UpdateStateStore(paths);
        Theme = new ThemeManager(Settings);
        ProcessRunner = new ProcessRunner();
        ToolLocator = new ToolLocator(paths);
        TaskManager = new TaskManager(paths, ProcessRunner);
        TaskConsole = new ViewModels.TaskConsoleViewModel(TaskManager);
        BilibiliMetadata = new BilibiliMetadataService(HttpClient);
        DownloadNaming = new DownloadNamingService();
        BBDown = new BBDownService(paths, ProcessRunner, ToolLocator, Settings, BilibiliMetadata, DownloadNaming);
        DualAudio = new DualAudioService(paths, BBDown, ProcessRunner, Settings, ToolLocator);
        Tmdb = new TmdbService(RenameSettings);
        Rename = new RenameService(ProcessRunner, ToolLocator, Settings, RenameHistory);
        AccountStatus = new AccountStatusService(paths, HttpClient);
        Updates = new UpdateService(UpdateHttpClient);
        UpdateCoordinator = new UpdateCoordinator(this);
    }

    public ApplicationPaths Paths { get; }
    public HttpClient HttpClient { get; }
    public HttpClient UpdateHttpClient { get; }
    public ISettingsStore Settings { get; }
    public IHistoryStore History { get; }
    public IRenameSettingsStore RenameSettings { get; }
    public IRenameHistoryStore RenameHistory { get; }
    public IUpdateStateStore UpdateState { get; }
    public ThemeManager Theme { get; }
    public IProcessRunner ProcessRunner { get; }
    public IToolLocator ToolLocator { get; }
    public ITaskManager TaskManager { get; }
    public ViewModels.TaskConsoleViewModel TaskConsole { get; }
    public IBilibiliMetadataService BilibiliMetadata { get; }
    public IDownloadNamingService DownloadNaming { get; }
    public IBBDownService BBDown { get; }
    public IDualAudioService DualAudio { get; }
    public ITmdbService Tmdb { get; }
    public IRenameService Rename { get; }
    public IAccountStatusService AccountStatus { get; }
    public IUpdateService Updates { get; }
    public UpdateCoordinator UpdateCoordinator { get; }
}
