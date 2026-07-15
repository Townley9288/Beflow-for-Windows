using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BBDownForWindows.App.ViewModels;

public sealed class HistoryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private HistoryRecord? _selected;
    private bool _isLoading;
    private readonly List<HistoryRecord> _allRecords = [];
    private int _pageNumber = 1;
    private int _suppressStoreChanges;
    private int _reloadQueued;
    private bool _active;
    private const int PageSize = 6;

    public HistoryViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ReloadCommand = new AsyncRelayCommand(LoadAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedRecord is not null);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => Records.Count > 0);
        RefreshTitlesCommand = new AsyncRelayCommand(RefreshTitlesAsync, () => Records.Count > 0);
        PreviousPageCommand = new RelayCommand(PreviousPage, () => PageNumber > 1);
        NextPageCommand = new RelayCommand(NextPage, () => PageNumber < TotalPages);
    }

    public ObservableCollection<HistoryRecord> Records { get; } = [];
    public HistoryRecord? SelectedRecord
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) DeleteCommand.NotifyCanExecuteChanged(); }
    }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public int PageNumber
    {
        get => _pageNumber;
        private set
        {
            if (SetProperty(ref _pageNumber, value))
            {
                OnPropertyChanged(nameof(PageText));
                PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_allRecords.Count / (double)PageSize));
    public string PageText => $"第 {PageNumber} / {TotalPages} 页";
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ClearCommand { get; }
    public IAsyncRelayCommand RefreshTitlesCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }

    public void Activate()
    {
        if (_active) return;
        _active = true;
        _services.History.Changed += History_Changed;
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _services.History.Changed -= History_Changed;
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            IsLoading = true;
            _allRecords.Clear();
            _allRecords.AddRange(await _services.History.LoadAsync());
            PageNumber = Math.Min(PageNumber, TotalPages);
            ApplyPage();
            ClearCommand.NotifyCanExecuteChanged(); RefreshTitlesCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }
    }

    private async Task DeleteAsync()
    {
        if (SelectedRecord is null) return;
        var index = _allRecords.IndexOf(SelectedRecord);
        await SuppressStoreChangeAsync(() => _services.History.DeleteAsync(index));
        await LoadAsync();
    }

    private async Task ClearAsync()
    {
        await SuppressStoreChangeAsync(() => _services.History.ClearAsync());
        await LoadAsync();
    }

    private async Task RefreshTitlesAsync()
    {
        IsLoading = true;
        foreach (var record in _allRecords.Where(record => string.IsNullOrWhiteSpace(record.Title) && record.TaskType != TaskKind.DualAudioRemux))
        {
            try { record.Title = await _services.BBDown.GetTitleAsync(record.Url, CancellationToken.None); } catch { }
        }
        await SuppressStoreChangeAsync(() => _services.History.SaveAllAsync(_allRecords));
        IsLoading = false;
        ApplyPage();
    }

    private async Task SuppressStoreChangeAsync(Func<Task> operation)
    {
        Interlocked.Increment(ref _suppressStoreChanges);
        try { await operation(); }
        finally { Interlocked.Decrement(ref _suppressStoreChanges); }
    }

    private void History_Changed(object? sender, EventArgs e)
    {
        if (!_active || Volatile.Read(ref _suppressStoreChanges) > 0 || Interlocked.Exchange(ref _reloadQueued, 1) != 0) return;
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                PageNumber = 1;
                await LoadAsync();
            }
            finally { Interlocked.Exchange(ref _reloadQueued, 0); }
        }))
            Interlocked.Exchange(ref _reloadQueued, 0);
    }

    private void PreviousPage() { if (PageNumber > 1) { PageNumber--; ApplyPage(); } }
    private void NextPage() { if (PageNumber < TotalPages) { PageNumber++; ApplyPage(); } }
    private void ApplyPage()
    {
        Records.Clear();
        foreach (var record in _allRecords.Skip((PageNumber - 1) * PageSize).Take(PageSize)) Records.Add(record);
        OnPropertyChanged(nameof(TotalPages)); OnPropertyChanged(nameof(PageText));
        PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
    }
}
