using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security;

namespace CondaManager.Core;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly CondaDiscoveryService _discovery;
    private readonly SelectionSettings _settings;
    private readonly Func<string?, IReadOnlyList<string>> _rootsProvider;
    private VerifiedInstallation? _selectedInstallation;
    private string _statusMessage = "准备扫描 Conda 安装";
    private bool _isScanning;
    private bool _hasNoResults;

    public MainViewModel(CondaDiscoveryService discovery, SelectionSettings settings,
        Func<string?, IReadOnlyList<string>> rootsProvider)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _rootsProvider = rootsProvider ?? throw new ArgumentNullException(nameof(rootsProvider));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<VerifiedInstallation> Installations { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];

    public VerifiedInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (value is not null && !Installations.Contains(value))
                return;
            if (ReferenceEquals(_selectedInstallation, value))
                return;

            _selectedInstallation = value;
            OnPropertyChanged();
            if (value is not null)
            {
                try
                {
                    _settings.Save(value.Info.RootPrefix);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  SecurityException or ArgumentException or NotSupportedException)
                {
                    Diagnostics.Add($"无法保存所选安装根目录：{exception.Message}");
                }
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    public bool HasNoResults
    {
        get => _hasNoResults;
        private set => SetProperty(ref _hasNoResults, value);
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning)
            return;

        var currentRoot = SelectedInstallation?.Info.RootPrefix;
        IsScanning = true;
        HasNoResults = false;
        StatusMessage = "正在查找 Conda 安装…";
        Installations.Clear();
        Diagnostics.Clear();
        SelectedInstallation = null;

        try
        {
            var savedRoot = await Task.Run(_settings.Load, cancellationToken).WaitAsync(cancellationToken);
            var roots = await Task.Run(() => _rootsProvider(savedRoot), cancellationToken)
                .WaitAsync(cancellationToken);
            var scanRoots = currentRoot is null
                ? roots
                : new[] { currentRoot }.Concat(roots).ToArray();
            var result = await _discovery.DiscoverAsync(scanRoots,
                new InlineProgress<CondaDiscoveryProgress>(UpdateProgress), cancellationToken);
            foreach (var installation in result.Installations)
                Installations.Add(installation);
            foreach (var failure in result.Failures)
                Diagnostics.Add(failure);

            SelectedInstallation = Installations.FirstOrDefault(installation =>
                string.Equals(installation.Info.RootPrefix, currentRoot, StringComparison.OrdinalIgnoreCase))
                ?? Installations.FirstOrDefault(installation =>
                    string.Equals(installation.Info.RootPrefix, savedRoot, StringComparison.OrdinalIgnoreCase))
                ?? Installations.FirstOrDefault();
            HasNoResults = Installations.Count == 0;
            StatusMessage = HasNoResults
                ? "未找到可用的 Conda 安装。请选择安装根目录。"
                : $"已找到 {Installations.Count} 个可用的 Conda 安装。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "扫描已取消。";
            throw;
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task AddManualRootAsync(string root, CancellationToken cancellationToken = default)
    {
        if (IsScanning)
            return;

        IsScanning = true;
        StatusMessage = $"正在验证安装根目录：{root}";
        try
        {
            var result = await _discovery.DiscoverAsync([root],
                new InlineProgress<CondaDiscoveryProgress>(UpdateProgress), cancellationToken);
            foreach (var failure in result.Failures)
                Diagnostics.Add(failure);

            var verified = result.Installations.FirstOrDefault();
            if (verified is null)
            {
                HasNoResults = Installations.Count == 0;
                StatusMessage = $"所选目录不是可用的 Conda 安装：{root}";
                return;
            }

            var existing = Installations.FirstOrDefault(installation =>
                string.Equals(installation.Info.RootPrefix, verified.Info.RootPrefix,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Installations.Add(verified);
                existing = verified;
            }

            SelectedInstallation = existing;
            HasNoResults = false;
            StatusMessage = $"已选择 Conda 安装：{existing.Info.RootPrefix}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "验证已取消。";
            throw;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void UpdateProgress(CondaDiscoveryProgress progress)
    {
        if (progress.Completed < progress.Total)
            StatusMessage = $"正在检查 {progress.Completed + 1}/{progress.Total}：{progress.Root}";
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
