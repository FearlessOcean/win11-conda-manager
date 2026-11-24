using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CondaManager.Core;

public enum BrowserLoadState
{
    Idle,
    Loading,
    Ready,
    Empty,
    Error
}

public sealed class EnvironmentBrowserViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICondaEnvironmentCatalog _catalog;
    private VerifiedInstallation? _installation;
    private CancellationTokenSource? _environmentCancellation;
    private CancellationTokenSource? _packageCancellation;
    private long _environmentGeneration;
    private long _packageGeneration;
    private IReadOnlyList<CondaPackage> _allPackages = [];
    private CondaEnvironment? _selectedEnvironment;
    private BrowserLoadState _environmentState;
    private BrowserLoadState _packageState;
    private string? _environmentError;
    private string? _packageError;
    private string? _pythonVersion;
    private string _packageSearchText = string.Empty;
    private string? _preferredPrefix;
    private int _totalPackageCount;
    private bool _disposed;

    public EnvironmentBrowserViewModel(ICondaEnvironmentCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CondaEnvironment> Environments { get; } = [];
    public ObservableCollection<CondaPackage> VisiblePackages { get; } = [];

    public CondaEnvironment? SelectedEnvironment
    {
        get => _selectedEnvironment;
        private set => SetProperty(ref _selectedEnvironment, value);
    }

    public BrowserLoadState EnvironmentState
    {
        get => _environmentState;
        private set => SetProperty(ref _environmentState, value);
    }

    public BrowserLoadState PackageState
    {
        get => _packageState;
        private set => SetProperty(ref _packageState, value);
    }

    public string? EnvironmentError
    {
        get => _environmentError;
        private set => SetProperty(ref _environmentError, value);
    }

    public string? PackageError
    {
        get => _packageError;
        private set => SetProperty(ref _packageError, value);
    }

    public string? PythonVersion
    {
        get => _pythonVersion;
        private set => SetProperty(ref _pythonVersion, value);
    }

    public int TotalPackageCount
    {
        get => _totalPackageCount;
        private set => SetProperty(ref _totalPackageCount, value);
    }

    public string PackageSearchText
    {
        get => _packageSearchText;
        set
        {
            if (SetProperty(ref _packageSearchText, value ?? string.Empty))
                UpdateVisiblePackages();
        }
    }

    public bool HasFilteredNoResults =>
        PackageState == BrowserLoadState.Ready &&
        TotalPackageCount > 0 &&
        VisiblePackages.Count == 0 &&
        !string.IsNullOrWhiteSpace(PackageSearchText);

    public Task SetInstallationAsync(VerifiedInstallation? installation,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (installation is null || _installation is null ||
            !string.Equals(_installation.Info.RootPrefix, installation.Info.RootPrefix,
                StringComparison.OrdinalIgnoreCase))
            _preferredPrefix = null;
        _installation = installation;
        return LoadEnvironmentsAsync(installation, _preferredPrefix, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return LoadEnvironmentsAsync(_installation,
            SelectedEnvironment?.Prefix ?? _preferredPrefix, cancellationToken);
    }

    public Task RefreshAsync(string preferredPrefix, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredPrefix);
        return LoadEnvironmentsAsync(_installation, preferredPrefix, cancellationToken);
    }

    public Task SelectEnvironmentAsync(CondaEnvironment? environment,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (environment is null)
        {
            CancelPackages();
            ClearPackageDetails();
            SelectedEnvironment = null;
            PackageState = BrowserLoadState.Idle;
            return Task.CompletedTask;
        }

        var current = Environments.FirstOrDefault(item =>
            string.Equals(item.Prefix, environment.Prefix, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            return Task.CompletedTask;

        if (string.Equals(SelectedEnvironment?.Prefix, current.Prefix,
                StringComparison.OrdinalIgnoreCase) &&
            PackageState is BrowserLoadState.Loading or BrowserLoadState.Ready or BrowserLoadState.Empty)
            return Task.CompletedTask;

        return LoadPackagesAsync(current, cancellationToken);
    }

    public Task RefreshDetailsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return SelectedEnvironment is { } selected
            ? LoadPackagesAsync(selected, cancellationToken)
            : Task.CompletedTask;
    }

    private async Task LoadEnvironmentsAsync(VerifiedInstallation? installation,
        string? preferredPrefix, CancellationToken cancellationToken)
    {
        CancelEnvironments();
        CancelPackages();
        ClearPackageDetails();
        SelectedEnvironment = null;
        PackageState = BrowserLoadState.Idle;
        Environments.Clear();
        EnvironmentError = null;

        if (installation is null)
        {
            EnvironmentState = BrowserLoadState.Idle;
            return;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _environmentCancellation = linked;
        var generation = _environmentGeneration;
        EnvironmentState = BrowserLoadState.Loading;

        try
        {
            // Await without ConfigureAwait(false): WPF callers resume on their dispatcher context.
            var environments = await _catalog.ListEnvironmentsAsync(installation, linked.Token);
            if (!IsCurrentEnvironmentRequest(linked, generation))
                return;

            foreach (var environment in environments)
                Environments.Add(environment);
            EnvironmentState = Environments.Count == 0 ? BrowserLoadState.Empty : BrowserLoadState.Ready;

            var selected = Environments.FirstOrDefault(environment =>
                    string.Equals(environment.Prefix, preferredPrefix, StringComparison.OrdinalIgnoreCase))
                ?? Environments.FirstOrDefault();
            if (selected is not null)
                await LoadPackagesAsync(selected, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A newer selection or the owning window has ended this request.
        }
        catch (Exception exception)
        {
            if (!IsCurrentEnvironmentRequest(linked, generation))
                return;
            EnvironmentError = exception.Message;
            EnvironmentState = BrowserLoadState.Error;
        }
    }

    private async Task LoadPackagesAsync(CondaEnvironment environment,
        CancellationToken cancellationToken)
    {
        var installation = _installation;
        if (installation is null)
            return;

        CancelPackages();
        ClearPackageDetails();
        _preferredPrefix = environment.Prefix;
        SelectedEnvironment = environment;
        PackageState = BrowserLoadState.Loading;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _packageCancellation = linked;
        var generation = _packageGeneration;

        try
        {
            var packages = await _catalog.ListPackagesAsync(installation, environment.Prefix, linked.Token);
            if (!IsCurrentPackageRequest(linked, generation))
                return;

            _allPackages = packages
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Build, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            TotalPackageCount = _allPackages.Count;
            PythonVersion = _allPackages.FirstOrDefault(package =>
                string.Equals(package.Name, "python", StringComparison.OrdinalIgnoreCase))?.Version
                ?? "未安装 Python";
            PackageState = _allPackages.Count == 0 ? BrowserLoadState.Empty : BrowserLoadState.Ready;
            UpdateVisiblePackages();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A newer environment or the owning window has ended this request.
        }
        catch (Exception exception)
        {
            if (!IsCurrentPackageRequest(linked, generation))
                return;
            PackageError = exception.Message;
            PackageState = BrowserLoadState.Error;
        }
    }

    private void ClearPackageDetails()
    {
        _allPackages = [];
        VisiblePackages.Clear();
        PackageError = null;
        PythonVersion = null;
        TotalPackageCount = 0;
        OnPropertyChanged(nameof(HasFilteredNoResults));
    }

    private void UpdateVisiblePackages()
    {
        var query = PackageSearchText.Trim();
        VisiblePackages.Clear();
        foreach (var package in _allPackages)
        {
            if (query.Length == 0 ||
                package.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Version.Contains(query, StringComparison.OrdinalIgnoreCase))
                VisiblePackages.Add(package);
        }
        OnPropertyChanged(nameof(HasFilteredNoResults));
    }

    private void CancelEnvironments()
    {
        _environmentGeneration++;
        var previous = _environmentCancellation;
        _environmentCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private void CancelPackages()
    {
        _packageGeneration++;
        var previous = _packageCancellation;
        _packageCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private bool IsCurrentEnvironmentRequest(CancellationTokenSource cancellation, long generation) =>
        !_disposed &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(_environmentCancellation, cancellation) &&
        generation == _environmentGeneration;

    private bool IsCurrentPackageRequest(CancellationTokenSource cancellation, long generation) =>
        !_disposed &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(_packageCancellation, cancellation) &&
        generation == _packageGeneration;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(PackageState))
            OnPropertyChanged(nameof(HasFilteredNoResults));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelEnvironments();
        CancelPackages();
    }
}
