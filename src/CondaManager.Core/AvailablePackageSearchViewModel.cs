using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CondaManager.Core;

public sealed class AvailablePackageSearchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICondaPackageSearch _search;
    private CancellationTokenSource? _requestCancellation;
    private long _generation;
    private string _query = string.Empty;
    private BrowserLoadState _state;
    private string? _error;
    private bool _truncated;
    private AvailablePackage? _selectedResult;
    private bool _disposed;

    public AvailablePackageSearchViewModel(ICondaPackageSearch search) =>
        _search = search ?? throw new ArgumentNullException(nameof(search));

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AvailablePackage> Items { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            ThrowIfDisposed();
            var next = value ?? string.Empty;
            if (_query == next)
                return;
            _query = next;
            OnPropertyChanged();
            Cancel();
        }
    }

    public BrowserLoadState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearching));
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (_error == value)
                return;
            _error = value;
            OnPropertyChanged();
        }
    }

    public bool Truncated
    {
        get => _truncated;
        private set
        {
            if (_truncated == value)
                return;
            _truncated = value;
            OnPropertyChanged();
        }
    }

    public bool IsSearching => State == BrowserLoadState.Loading;

    public AvailablePackage? SelectedResult
    {
        get => _selectedResult;
        set
        {
            var next = value is not null && Items.Any(item => ReferenceEquals(item, value)) ? value : null;
            if (ReferenceEquals(_selectedResult, next))
                return;
            _selectedResult = next;
            OnPropertyChanged();
        }
    }

    public async Task SearchAsync(VerifiedInstallation installation, ChannelSnapshot channels,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(channels);

        CancelCurrentRequest();
        SelectedResult = null;
        Items.Clear();
        Error = null;
        Truncated = false;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestCancellation = linked;
        var generation = _generation;
        var query = Query;
        State = BrowserLoadState.Loading;
        try
        {
            // WPF callers must resume on the dispatcher to update observable collections.
            var page = await _search.SearchAsync(installation, query, channels, linked.Token);
            if (!IsCurrent(linked, generation))
                return;

            foreach (var item in page.Items)
                Items.Add(item);
            Truncated = page.Truncated;
            State = Items.Count == 0 ? BrowserLoadState.Empty : BrowserLoadState.Ready;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A newer query or an explicit cancellation owns the visible state.
        }
        catch (Exception exception)
        {
            if (!IsCurrent(linked, generation))
                return;
            Error = exception is CondaOperationException
                ? exception.Message
                : "Conda 软件包搜索失败。";
            State = BrowserLoadState.Error;
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, linked) && linked.IsCancellationRequested &&
                State == BrowserLoadState.Loading)
                State = Items.Count == 0 ? BrowserLoadState.Idle : BrowserLoadState.Ready;
        }
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        CancelCurrentRequest();
        if (State == BrowserLoadState.Loading)
            State = Items.Count == 0 ? BrowserLoadState.Idle : BrowserLoadState.Ready;
    }

    public void Invalidate()
    {
        ThrowIfDisposed();
        CancelCurrentRequest();
        SelectedResult = null;
        Items.Clear();
        Error = null;
        Truncated = false;
        State = BrowserLoadState.Idle;
    }

    private void CancelCurrentRequest()
    {
        _generation++;
        var previous = _requestCancellation;
        _requestCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private bool IsCurrent(CancellationTokenSource cancellation, long generation) =>
        !_disposed && !cancellation.IsCancellationRequested &&
        ReferenceEquals(_requestCancellation, cancellation) && generation == _generation;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelCurrentRequest();
    }
}
