using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed record RenameTemplateSeparatorOption(string Label, string Value);

public sealed record RenameTemplatesNavigationContext(RenameMediaType MediaType, string? TemplateId);

public sealed class RenameTemplateListItem
{
    public RenameTemplateListItem(RenameTemplate template, bool isActive)
    {
        Template = template;
        IsActive = isActive;
    }

    public RenameTemplate Template { get; }
    public string Name => Template.Name;
    public string Pattern => Template.Pattern;
    public bool IsActive { get; }
    public string Description => IsActive
        ? $"{(Template.BuiltIn ? "内置模板" : "自定义模板")} · 当前应用"
        : Template.BuiltIn ? "内置模板" : "自定义模板";
}

internal static class RenameTemplatePresentation
{
    public static IReadOnlyList<string> FieldOptions { get; } =
    ["中文名", "英文名", "年份", "季", "集", "集名", "分辨率", "来源", "动态范围", "编码", "音频", "帧率", "扩展名"];

    public static IReadOnlyList<RenameTemplateSeparatorOption> SeparatorOptions { get; } =
    [new("点号 (.)", "."), new("空格", " "), new("下划线 (_)", "_"), new("中划线 (-)", "-"), new("无", "")];

    public static string BuildExample(string pattern)
    {
        var result = pattern;
        foreach (var pair in new Dictionary<string, string>
        {
            ["{中文名}"] = "流人", ["{英文名}"] = "Slow Horses", ["{年份}"] = "2024",
            ["{季}"] = "S04", ["{集}"] = "E01", ["{集名}"] = "身份盗窃", ["{分辨率}"] = "2160p",
            ["{来源}"] = "WEB-DL", ["{动态范围}"] = "DV", ["{编码}"] = "HEVC",
            ["{音频}"] = "DDP.5.1.Atmos", ["{帧率}"] = "60fps", ["{扩展名}"] = ".mkv"
        }) result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return result;
    }
}

public sealed class RenameTemplatesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private RenameSettings _settings = new();
    private RenameMediaType _mediaType = RenameMediaType.Series;
    private RenameTemplateListItem? _selectedItem;
    private string _templatePattern = string.Empty;
    private RenameTemplateSeparatorOption? _selectedSeparator;
    private string _message = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;
    private bool _loadingEditor;

    public RenameTemplatesViewModel(AppServices services) => _services = services;

    public IReadOnlyList<string> MediaTypeOptions { get; } = ["剧集", "电影"];
    public IReadOnlyList<string> FieldOptions => RenameTemplatePresentation.FieldOptions;
    public IReadOnlyList<RenameTemplateSeparatorOption> SeparatorOptions => RenameTemplatePresentation.SeparatorOptions;
    public ObservableCollection<RenameTemplateListItem> Templates { get; } = [];
    public ObservableCollection<string> TemplateFields { get; } = [];

    public RenameMediaType MediaType => _mediaType;
    public string MediaTypeText => _mediaType == RenameMediaType.Series ? "剧集" : "电影";

    public RenameTemplateListItem? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            LoadEditor(value?.Template.Pattern ?? string.Empty);
            NotifyTemplateState();
        }
    }

    public RenameTemplate? SelectedTemplate => SelectedItem?.Template;

    public string TemplatePattern
    {
        get => _templatePattern;
        set
        {
            if (!SetProperty(ref _templatePattern, value)) return;
            OnPropertyChanged(nameof(TemplateExample));
            if (!_loadingEditor) NotifyTemplateState();
        }
    }

    public string TemplateExample => RenameTemplatePresentation.BuildExample(TemplatePattern);

    public RenameTemplateSeparatorOption? SelectedSeparator
    {
        get => _selectedSeparator;
        set => SetProperty(ref _selectedSeparator, value);
    }

    public bool HasUnsavedChanges => SelectedTemplate is not null &&
        !string.Equals(TemplatePattern, SelectedTemplate.Pattern, StringComparison.Ordinal);
    public Visibility UnsavedChangesVisibility => HasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;
    public bool HasSelectedTemplate => SelectedTemplate is not null;
    public bool IsBuiltIn => SelectedTemplate?.BuiltIn == true;
    public bool IsActive => SelectedItem?.IsActive == true;
    public bool CanSaveChanges => SelectedTemplate is { BuiltIn: false } && HasUnsavedChanges;
    public bool CanSaveAs => SelectedTemplate is not null && !string.IsNullOrWhiteSpace(TemplatePattern);
    public bool CanRename => SelectedTemplate is { BuiltIn: false } && !HasUnsavedChanges;
    public bool CanDelete => SelectedTemplate is { BuiltIn: false } && !HasUnsavedChanges;
    public bool CanSetActive => SelectedTemplate is not null && !IsActive && !HasUnsavedChanges;
    public string SelectedTemplateTitle => SelectedTemplate?.Name ?? "未选择模板";
    public string EditingHint => IsBuiltIn
        ? "内置模板不会被覆盖；调整规则后请使用“另存为并应用”。"
        : "自定义模板可以保存修改、另存、重命名或删除。";
    public string ActiveTemplateText
    {
        get
        {
            var activeId = GetActiveTemplateId(_mediaType);
            var name = _settings.Templates.FirstOrDefault(template => template.Id == activeId)?.Name ?? "内置模板";
            return $"当前应用：{name}";
        }
    }

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

    public async Task InitializeAsync(RenameTemplatesNavigationContext? context)
    {
        var preferredId = context?.TemplateId ?? SelectedTemplate?.Id;
        if (context is not null) _mediaType = context.MediaType;
        _settings = await _services.RenameSettings.LoadAsync();
        OnPropertyChanged(nameof(MediaType));
        OnPropertyChanged(nameof(MediaTypeText));
        ReloadTemplates(preferredId ?? GetActiveTemplateId(_mediaType));
    }

    public void ChangeMediaType(RenameMediaType mediaType)
    {
        if (_mediaType == mediaType) return;
        _mediaType = mediaType;
        OnPropertyChanged(nameof(MediaType));
        OnPropertyChanged(nameof(MediaTypeText));
        ReloadTemplates(GetActiveTemplateId(mediaType));
    }

    public void SelectTemplate(RenameTemplateListItem? item) => SelectedItem = item;

    public void DiscardChanges()
    {
        if (SelectedTemplate is not null) LoadEditor(SelectedTemplate.Pattern);
    }

    public async Task SaveChangesAsync()
    {
        var selected = SelectedTemplate is { BuiltIn: false } template ? template : throw new InvalidOperationException("内置模板不能被覆盖");
        ValidatePattern();
        var pattern = TemplatePattern;
        _settings = await _services.RenameSettings.UpdateAsync(current =>
        {
            var stored = current.Templates.FirstOrDefault(item => item.Id == selected.Id) ?? throw new InvalidOperationException("模板已不存在，请重新加载");
            stored.Pattern = pattern;
            return current;
        });
        ReloadTemplates(selected.Id);
        SetMessage("模板修改已保存", InfoBarSeverity.Success);
    }

    public async Task CreateTemplateAsync(string name)
    {
        var normalizedName = ValidateName(name);
        var template = new RenameTemplate
        {
            Name = normalizedName,
            MediaType = _mediaType,
            Pattern = SelectedTemplate?.Pattern ?? (_mediaType == RenameMediaType.Series
                ? RenameTemplate.SeriesDefault().Pattern
                : RenameTemplate.MovieDefault().Pattern),
            BuiltIn = false
        };
        _settings = await _services.RenameSettings.UpdateAsync(current =>
        {
            EnsureUniqueName(current, normalizedName, _mediaType);
            current.Templates.Add(template);
            return current;
        });
        ReloadTemplates(template.Id);
        SetMessage("已基于当前规则新建模板；当前应用模板未改变", InfoBarSeverity.Success);
    }

    public async Task SaveAsAndApplyAsync(string name)
    {
        ValidatePattern();
        var normalizedName = ValidateName(name);
        var template = new RenameTemplate
        {
            Name = normalizedName,
            MediaType = _mediaType,
            Pattern = TemplatePattern,
            BuiltIn = false
        };
        _settings = await _services.RenameSettings.UpdateAsync(current =>
        {
            EnsureUniqueName(current, normalizedName, _mediaType);
            current.Templates.Add(template);
            SetActiveTemplateId(current, _mediaType, template.Id);
            return current;
        });
        ReloadTemplates(template.Id);
        SetMessage("已另存为自定义模板并设为当前应用", InfoBarSeverity.Success);
    }

    public async Task RenameSelectedAsync(string name)
    {
        var selected = SelectedTemplate is { BuiltIn: false } template ? template : throw new InvalidOperationException("内置模板不能重命名");
        if (HasUnsavedChanges) throw new InvalidOperationException("请先保存或放弃当前规则修改");
        var normalizedName = ValidateName(name);
        _settings = await _services.RenameSettings.UpdateAsync(current =>
        {
            EnsureUniqueName(current, normalizedName, _mediaType, selected.Id);
            var stored = current.Templates.FirstOrDefault(item => item.Id == selected.Id) ?? throw new InvalidOperationException("模板已不存在，请重新加载");
            stored.Name = normalizedName;
            return current;
        });
        ReloadTemplates(selected.Id);
        SetMessage("模板已重命名", InfoBarSeverity.Success);
    }

    public async Task DeleteSelectedAsync()
    {
        var selected = SelectedTemplate is { BuiltIn: false } template ? template : throw new InvalidOperationException("内置模板不能删除");
        if (HasUnsavedChanges) throw new InvalidOperationException("请先放弃当前规则修改再删除模板");
        _settings = await _services.RenameSettings.UpdateAsync(current =>
        {
            current.Templates.RemoveAll(item => item.Id == selected.Id);
            if (current.ActiveSeriesTemplateId == selected.Id) current.ActiveSeriesTemplateId = RenameTemplate.BuiltInSeriesId;
            if (current.ActiveMovieTemplateId == selected.Id) current.ActiveMovieTemplateId = RenameTemplate.BuiltInMovieId;
            return current;
        });
        ReloadTemplates(GetActiveTemplateId(_mediaType));
        SetMessage("自定义模板已删除", InfoBarSeverity.Informational);
    }

    public async Task SetActiveAsync()
    {
        var selected = SelectedTemplate ?? throw new InvalidOperationException("请先选择模板");
        if (HasUnsavedChanges) throw new InvalidOperationException("请先保存或放弃当前规则修改");
        _settings = await _services.RenameSettings.UpdateAsync(current =>
        {
            SetActiveTemplateId(current, selected.MediaType, selected.Id);
            return current;
        });
        ReloadTemplates(selected.Id);
        SetMessage($"已将“{selected.Name}”设为当前模板", InfoBarSeverity.Success);
    }

    public void AddTemplateField(string field)
    {
        if (!FieldOptions.Contains(field) || TemplateFields.Contains(field)) return;
        TemplateFields.Add(field);
        ApplyFieldLayout();
    }

    public void RemoveTemplateField(string field)
    {
        TemplateFields.Remove(field);
        ApplyFieldLayout();
    }

    public void MoveTemplateField(string field, int offset)
    {
        var index = TemplateFields.IndexOf(field);
        if (index < 0) return;
        var target = Math.Clamp(index + offset, 0, TemplateFields.Count - 1);
        if (target == index) return;
        TemplateFields.Move(index, target);
        ApplyFieldLayout();
    }

    public void ApplyFieldLayout()
    {
        var separator = SelectedSeparator?.Value ?? ".";
        var parts = new List<string>();
        for (var index = 0; index < TemplateFields.Count; index++)
        {
            if (index > 0 && !(TemplateFields[index - 1] == "季" && TemplateFields[index] == "集")) parts.Add(separator);
            parts.Add($"{{{TemplateFields[index]}}}");
        }
        TemplatePattern = string.Concat(parts);
    }

    public void ReadFieldsFromPattern()
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(TemplatePattern, @"\{([^}]+)\}");
        TemplateFields.Clear();
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var field = match.Groups[1].Value;
            if (FieldOptions.Contains(field) && !TemplateFields.Contains(field)) TemplateFields.Add(field);
        }
        var separator = ".";
        for (var index = 1; index < matches.Count; index++)
        {
            var previous = matches[index - 1].Groups[1].Value;
            var current = matches[index].Groups[1].Value;
            if (previous == "季" && current == "集") continue;
            var betweenStart = matches[index - 1].Index + matches[index - 1].Length;
            var between = TemplatePattern.Substring(betweenStart, matches[index].Index - betweenStart);
            if (SeparatorOptions.Any(option => option.Value == between)) separator = between;
            break;
        }
        SelectedSeparator = SeparatorOptions.First(option => option.Value == separator);
    }

    public void ReportError(Exception exception) => SetMessage(exception.Message, InfoBarSeverity.Error);

    private void ReloadTemplates(string? selectedId)
    {
        var activeId = GetActiveTemplateId(_mediaType);
        Templates.Clear();
        foreach (var template in _settings.Templates
                     .Where(template => template.MediaType == _mediaType)
                     .OrderByDescending(template => template.BuiltIn)
                     .ThenBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase))
            Templates.Add(new RenameTemplateListItem(template, template.Id == activeId));
        SelectedItem = Templates.FirstOrDefault(item => item.Template.Id == selectedId)
            ?? Templates.FirstOrDefault(item => item.Template.Id == activeId)
            ?? Templates.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveTemplateText));
    }

    private void LoadEditor(string pattern)
    {
        _loadingEditor = true;
        TemplatePattern = pattern;
        ReadFieldsFromPattern();
        _loadingEditor = false;
        NotifyTemplateState();
    }

    private void NotifyTemplateState()
    {
        OnPropertyChanged(nameof(SelectedTemplate));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedChangesVisibility));
        OnPropertyChanged(nameof(HasSelectedTemplate));
        OnPropertyChanged(nameof(IsBuiltIn));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanSaveChanges));
        OnPropertyChanged(nameof(CanSaveAs));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanSetActive));
        OnPropertyChanged(nameof(SelectedTemplateTitle));
        OnPropertyChanged(nameof(EditingHint));
    }

    private string GetActiveTemplateId(RenameMediaType mediaType) => mediaType == RenameMediaType.Series
        ? _settings.ActiveSeriesTemplateId
        : _settings.ActiveMovieTemplateId;

    private static void SetActiveTemplateId(RenameSettings settings, RenameMediaType mediaType, string id)
    {
        if (mediaType == RenameMediaType.Series) settings.ActiveSeriesTemplateId = id;
        else settings.ActiveMovieTemplateId = id;
    }

    private string ValidateName(string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException("模板名称不能为空");
        return normalized;
    }

    private void ValidatePattern()
    {
        RenameService.ValidateTemplatePattern(TemplatePattern);
    }

    private static void EnsureUniqueName(RenameSettings settings, string name, RenameMediaType mediaType, string? excludedId = null)
    {
        if (settings.Templates.Any(template => template.MediaType == mediaType && template.Id != excludedId &&
                                               string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"当前{(mediaType == RenameMediaType.Series ? "剧集" : "电影")}模板中已存在同名项目");
    }

    private void SetMessage(string message, InfoBarSeverity severity)
    {
        MessageSeverity = severity;
        Message = message;
    }
}
