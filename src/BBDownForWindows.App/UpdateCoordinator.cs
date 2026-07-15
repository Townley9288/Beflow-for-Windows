using System.Diagnostics;
using BBDownForWindows.Core;

namespace BBDownForWindows.App;

public sealed class UpdateCoordinator
{
    private readonly AppServices _services;
    private bool _isBusy;
    private double _progress;
    private string _statusText = "尚未检查更新";
    private UpdateRelease? _availableRelease;

    public UpdateCoordinator(AppServices services)
    {
        _services = services;
        CleanupOldDownloads();
    }

    public event EventHandler? StateChanged;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; RaiseChanged(); } }
    public double Progress { get => _progress; private set { _progress = value; RaiseChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; RaiseChanged(); } }
    public UpdateRelease? AvailableRelease { get => _availableRelease; private set { _availableRelease = value; RaiseChanged(); } }
    public bool HasUpdate => AvailableRelease is not null;

    public async Task CheckOnStartupAsync()
    {
        if (Debugger.IsAttached) return;
        try
        {
            var settings = await _services.Settings.LoadAsync();
            var state = await _services.UpdateState.LoadAsync();
            if (!UpdateSchedule.ShouldCheck(settings.CheckUpdatesOnStartup, state.LastCheckedAt, DateTimeOffset.UtcNow)) return;
            await CheckAsync(false);
        }
        catch
        {
            // Automatic checks never block startup or disturb the download workflow.
        }
    }

    public async Task<UpdateCheckResult?> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return null;
        IsBusy = true;
        StatusText = "正在检查 GitHub Releases…";
        try
        {
            var result = await _services.Updates.CheckAsync(AppVersion.Current, cancellationToken);
            AvailableRelease = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Release : null;
            StatusText = result.Message;
            await _services.UpdateState.SaveAsync(new UpdateState { LastCheckedAt = DateTimeOffset.UtcNow }, cancellationToken);
            if (AvailableRelease is not null)
                ((App)Microsoft.UI.Xaml.Application.Current).MainWindow.ShowUpdateAvailable(AvailableRelease);
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            StatusText = manual ? $"检查更新失败：{exception.Message}" : "暂时无法检查更新";
            try { await _services.UpdateState.SaveAsync(new UpdateState { LastCheckedAt = DateTimeOffset.UtcNow }, cancellationToken); } catch { }
            if (manual) throw;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApplyAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (AvailableRelease is null) throw new InvalidOperationException("当前没有可安装的新版本。");
        if (_services.TaskManager.ActiveTask?.State == TaskState.Running)
            throw new InvalidOperationException("当前有任务正在运行，请等待任务完成或取消后再更新。");
        if (IsBusy) return;

        IsBusy = true;
        Progress = 0;
        var release = AvailableRelease;
        var asset = _services.Paths.Portable ? release.Portable : release.Installer;
        var updateDirectory = Path.Combine(Path.GetTempPath(), "Beflow", "Updates", release.TagName + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (_services.Paths.Portable)
            {
                try { EnsurePortableDirectoryWritable(); }
                catch
                {
                    Process.Start(new ProcessStartInfo { FileName = release.ReleasePage.ToString(), UseShellExecute = true });
                    throw;
                }
            }
            StatusText = $"正在下载 {asset.FileName}…";
            var progress = new Progress<double>(value => Progress = value);
            var package = await _services.Updates.DownloadAndVerifyAsync(asset, updateDirectory, progress, cancellationToken);
            StatusText = "更新包校验通过，正在启动更新…";
            if (_services.Paths.Portable)
                StartPortableUpdater(package, updateDirectory);
            else
                StartInstaller(package);
            ((App)Microsoft.UI.Xaml.Application.Current).MainWindow.RequestUpdateShutdown();
        }
        catch
        {
            IsBusy = false;
            throw;
        }
    }

    private void StartInstaller(string package)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = package,
            UseShellExecute = true,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
        });
    }

    private void StartPortableUpdater(string package, string updateDirectory)
    {
        var sourceUpdater = Path.Combine(_services.Paths.ApplicationDirectory, "Beflow.Updater.exe");
        if (!File.Exists(sourceUpdater)) throw new FileNotFoundException("便携版缺少 Beflow.Updater.exe，无法自动替换文件。", sourceUpdater);
        var temporaryUpdater = Path.Combine(updateDirectory, "Beflow.Updater.exe");
        File.Copy(sourceUpdater, temporaryUpdater, true);
        var log = Path.Combine(_services.Paths.LogsDirectory, $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var startInfo = new ProcessStartInfo { FileName = temporaryUpdater, UseShellExecute = false, WorkingDirectory = updateDirectory };
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(package);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(_services.Paths.ApplicationDirectory);
        startInfo.ArgumentList.Add("--restart");
        startInfo.ArgumentList.Add(Path.Combine(_services.Paths.ApplicationDirectory, "Beflow.exe"));
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(log);
        Process.Start(startInfo);
    }

    private void EnsurePortableDirectoryWritable()
    {
        var probe = Path.Combine(_services.Paths.ApplicationDirectory, $".beflow-update-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(probe, string.Empty); }
        catch (Exception exception) { throw new UnauthorizedAccessException("便携版目录不可写，请以可写目录运行或手动下载更新。", exception); }
        finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
    }

    private static void CleanupOldDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "Beflow", "Updates");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) Directory.Delete(directory, true);
            }
            catch { }
        }
    }

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
