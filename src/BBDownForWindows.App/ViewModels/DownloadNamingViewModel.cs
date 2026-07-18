using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed record DownloadNamingNavigationContext(DownloadNamingProfileKind ProfileKind);

public sealed class DownloadNamingFieldGroup(string name, IReadOnlyList<DownloadNamingField> fields)
{
    public string Name { get; } = name;
    public IReadOnlyList<DownloadNamingField> Fields { get; } = fields;
}

public sealed class DownloadNamingViewModel : ObservableObject
{
    private readonly AppServices _services;
    private DownloadNamingProfile _singleDraft = DownloadNamingProfile.Default();
    private DownloadNamingProfile _multiDraft = DownloadNamingProfile.Default();
    private DownloadNamingProfile _savedSingle = DownloadNamingProfile.Default();
    private DownloadNamingProfile _savedMulti = DownloadNamingProfile.Default();
    private DownloadNamingProfileKind _profileKind;
    private string _workDirectory = string.Empty;
    private string _message = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    public DownloadNamingViewModel(AppServices services)
    {
        _services = services;
        FieldGroups = services.DownloadNaming.Fields
            .GroupBy(field => field.Category)
            .Select(group => new DownloadNamingFieldGroup(group.Key, group.ToList()))
            .ToList();
    }

    public IReadOnlyList<DownloadNamingFieldGroup> FieldGroups { get; }
    public DownloadNamingProfileKind ProfileKind => _profileKind;
    public string ProfileKindText => _profileKind == DownloadNamingProfileKind.MultiEpisode ? "多集内容" : "单集视频";
    public string ProfileDescription => _profileKind == DownloadNamingProfileKind.MultiEpisode
        ? "用于番剧、多分 P 和其他总分集数大于 1 的内容，即使本次只下载其中一集也会使用此规则。"
        : "用于 BBDown 返回总分集数为 1 的普通视频。";

    public string MainFolderTemplate
    {
        get => CurrentDraft.MainFolderTemplate;
        set
        {
            if (CurrentDraft.MainFolderTemplate == value) return;
            CurrentDraft.MainFolderTemplate = value;
            NotifyDraftChanged();
        }
    }

    public string SubfolderTemplate
    {
        get => CurrentDraft.SubfolderTemplate;
        set
        {
            if (CurrentDraft.SubfolderTemplate == value) return;
            CurrentDraft.SubfolderTemplate = value;
            NotifyDraftChanged();
        }
    }

    public string FileNameTemplate
    {
        get => CurrentDraft.FileNameTemplate;
        set
        {
            if (CurrentDraft.FileNameTemplate == value) return;
            CurrentDraft.FileNameTemplate = value;
            NotifyDraftChanged();
        }
    }

    public string PreviewPath
    {
        get
        {
            var preview = CurrentPreview;
            return preview.IsValid ? preview.RelativePath + ".mkv" : "无法生成示例路径";
        }
    }

    public string ValidationError => CurrentPreview.Error;
    public string WarningsText => string.Join("；", CurrentPreview.Warnings);
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ValidationError) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WarningsVisibility => string.IsNullOrWhiteSpace(WarningsText) ? Visibility.Collapsed : Visibility.Visible;
    public bool HasUnsavedChanges => !ProfileEquals(_singleDraft, _savedSingle) || !ProfileEquals(_multiDraft, _savedMulti);
    public Visibility UnsavedChangesVisibility => HasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSave => HasUnsavedChanges &&
        _services.DownloadNaming.Validate(_singleDraft).IsValid &&
        _services.DownloadNaming.Validate(_multiDraft).IsValid;

    public string Message
    {
        get => _message;
        private set
        {
            if (!SetProperty(ref _message, value)) return;
            OnPropertyChanged(nameof(HasMessage));
            OnPropertyChanged(nameof(MessageVisibility));
        }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public Visibility MessageVisibility => HasMessage ? Visibility.Visible : Visibility.Collapsed;
    public InfoBarSeverity MessageSeverity { get => _messageSeverity; private set => SetProperty(ref _messageSeverity, value); }

    public async Task InitializeAsync(DownloadNamingNavigationContext? context = null)
    {
        var settings = await _services.Settings.LoadAsync();
        settings.DownloadNaming.EnsureDefaults();
        _savedSingle = settings.DownloadNaming.SingleVideo.Clone();
        _savedMulti = settings.DownloadNaming.MultiEpisode.Clone();
        _singleDraft = _savedSingle.Clone();
        _multiDraft = _savedMulti.Clone();
        _workDirectory = settings.WorkDirectory;
        _profileKind = context?.ProfileKind ?? _profileKind;
        Message = string.Empty;
        NotifyAll();
    }

    public void ChangeProfile(DownloadNamingProfileKind kind)
    {
        if (_profileKind == kind) return;
        _profileKind = kind;
        Message = string.Empty;
        NotifyAll();
    }

    public async Task SaveAsync()
    {
        ValidateProfile(_singleDraft, "单集视频");
        ValidateProfile(_multiDraft, "多集内容");
        var single = _singleDraft.Clone();
        var multi = _multiDraft.Clone();
        var saved = await _services.Settings.UpdateAsync(current =>
        {
            var snapshot = current.Clone();
            snapshot.SchemaVersion = 4;
            snapshot.DownloadNaming.SingleVideo = single.Clone();
            snapshot.DownloadNaming.MultiEpisode = multi.Clone();
            return snapshot;
        });
        _savedSingle = saved.DownloadNaming.SingleVideo.Clone();
        _savedMulti = saved.DownloadNaming.MultiEpisode.Clone();
        _singleDraft = _savedSingle.Clone();
        _multiDraft = _savedMulti.Clone();
        SetMessage("下载命名规则已保存", InfoBarSeverity.Success);
        NotifyAll();
    }

    public void RestoreCurrentDefault()
    {
        if (_profileKind == DownloadNamingProfileKind.MultiEpisode) _multiDraft = DownloadNamingProfile.Default();
        else _singleDraft = DownloadNamingProfile.Default();
        Message = string.Empty;
        NotifyAll();
    }

    public void DiscardChanges()
    {
        _singleDraft = _savedSingle.Clone();
        _multiDraft = _savedMulti.Clone();
        Message = string.Empty;
        NotifyAll();
    }

    public void ReportError(Exception exception) => SetMessage(exception.Message, InfoBarSeverity.Error);

    private DownloadNamingProfile CurrentDraft => _profileKind == DownloadNamingProfileKind.MultiEpisode ? _multiDraft : _singleDraft;
    private DownloadNamingPreview CurrentPreview => _services.DownloadNaming.Preview(CurrentDraft, _profileKind,
        string.IsNullOrWhiteSpace(_workDirectory) ? Path.GetTempPath() : _workDirectory);

    private void ValidateProfile(DownloadNamingProfile profile, string label)
    {
        var result = _services.DownloadNaming.Validate(profile);
        if (!result.IsValid) throw new InvalidOperationException($"{label}规则：{result.Error}");
    }

    private void NotifyDraftChanged()
    {
        Message = string.Empty;
        NotifyPreviewState();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(ProfileKind));
        OnPropertyChanged(nameof(ProfileKindText));
        OnPropertyChanged(nameof(ProfileDescription));
        OnPropertyChanged(nameof(MainFolderTemplate));
        OnPropertyChanged(nameof(SubfolderTemplate));
        OnPropertyChanged(nameof(FileNameTemplate));
        NotifyPreviewState();
    }

    private void NotifyPreviewState()
    {
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(ErrorVisibility));
        OnPropertyChanged(nameof(WarningsVisibility));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedChangesVisibility));
        OnPropertyChanged(nameof(CanSave));
    }

    private void SetMessage(string message, InfoBarSeverity severity)
    {
        MessageSeverity = severity;
        Message = message;
    }

    private static bool ProfileEquals(DownloadNamingProfile left, DownloadNamingProfile right) =>
        string.Equals(left.MainFolderTemplate, right.MainFolderTemplate, StringComparison.Ordinal) &&
        string.Equals(left.SubfolderTemplate, right.SubfolderTemplate, StringComparison.Ordinal) &&
        string.Equals(left.FileNameTemplate, right.FileNameTemplate, StringComparison.Ordinal);
}
