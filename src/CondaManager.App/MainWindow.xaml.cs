using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;
using Microsoft.Win32;

namespace CondaManager.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly IManagedPrefixStore _managedPrefixes;
    private readonly ICondaEnvironmentOperator _operator;
    private CancellationTokenSource? _operationCancellation;
    private bool _isOperating;
    private bool _canCancelOperation;
    private bool _operationNeedsInspection;
    private bool _isOperationError;
    private string _operationStatusMessage = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    public MainViewModel Discovery { get; }
    public EnvironmentBrowserViewModel Browser { get; }

    public bool IsOperating
    {
        get => _isOperating;
        private set
        {
            if (_isOperating == value)
                return;
            _isOperating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(CanDelete));
            NotifyPackageActionsChanged();
        }
    }

    public bool CanInteract => !IsOperating;
    public bool CanCreate => CanInteract && !_operationNeedsInspection &&
        !Discovery.IsScanning && Discovery.SelectedInstallation is not null;

    public bool CanDelete
    {
        get
        {
            var selected = Browser.SelectedEnvironment;
            if (!CanCreate || selected is null || selected.IsBase)
                return false;
            if (WindowsPathSafety.IsUnsupportedAlias(selected.Prefix) ||
                Discovery.Installations.Any(item =>
                    WindowsPathSafety.IsUnsupportedAlias(item.Info.RootPrefix)))
                return false;
            var activePrefix = Environment.GetEnvironmentVariable("CONDA_PREFIX");
            if (WindowsPathSafety.IsUnsupportedAlias(activePrefix))
                return false;
            if (!string.IsNullOrWhiteSpace(activePrefix) && Path.IsPathFullyQualified(activePrefix) &&
                IsSameOrAncestor(selected.Prefix, activePrefix))
                return false;
            return !Discovery.Installations.Any(installation =>
                IsSameOrAncestor(selected.Prefix, installation.Info.RootPrefix));
        }
    }

    public bool CanCancelOperation
    {
        get => _canCancelOperation;
        private set
        {
            if (_canCancelOperation == value)
                return;
            _canCancelOperation = value;
            OnPropertyChanged();
        }
    }

    public bool IsOperationError
    {
        get => _isOperationError;
        private set
        {
            if (_isOperationError == value)
                return;
            _isOperationError = value;
            OnPropertyChanged();
        }
    }

    public string OperationStatusMessage
    {
        get => _operationStatusMessage;
        private set
        {
            if (_operationStatusMessage == value)
                return;
            _operationStatusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOperationMessage));
        }
    }

    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationStatusMessage);

    public MainWindow()
    {
        var runner = new ProcessRunner();
        var settings = new SelectionSettings();
        _managedPrefixes = new ManagedPrefixStore();
        _operator = new CondaEnvironmentOperator(runner);
        _packageOperator = new CondaPackageOperator(runner);
        var discovery = new CondaDiscoveryService(new CondaLauncherValidator(runner));
        Discovery = new MainViewModel(discovery, settings, savedRoot =>
            CondaCandidateFinder.FindRoots(
                savedRoot,
                Environment.GetEnvironmentVariable("CONDA_EXE"),
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        Browser = new EnvironmentBrowserViewModel(new CondaEnvironmentCatalog(runner, _managedPrefixes));
        Discovery.PropertyChanged += Discovery_PropertyChanged;
        Browser.PropertyChanged += Browser_PropertyChanged;
        InitializeComponent();
        DataContext = this;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Conda 安装根目录" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await Discovery.AddManualRootAsync(dialog.FolderName, _windowCancellation.Token);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法验证 Conda 安装", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ScanAsync()
    {
        try
        {
            await Discovery.ScanAsync(_windowCancellation.Token);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "扫描 Conda 安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Discovery_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedInstallation) or nameof(MainViewModel.IsScanning))
        {
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(CanDelete));
            NotifyPackageActionsChanged();
        }
        if (e.PropertyName == nameof(MainViewModel.SelectedInstallation))
            await RunBrowserAsync(token => Browser.SetInstallationAsync(Discovery.SelectedInstallation, token));
    }

    private void Browser_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvironmentBrowserViewModel.SelectedEnvironment))
            OnPropertyChanged(nameof(CanDelete));
        if (e.PropertyName is nameof(EnvironmentBrowserViewModel.SelectedEnvironment) or
            nameof(EnvironmentBrowserViewModel.EnvironmentState) or nameof(EnvironmentBrowserViewModel.PackageState))
            NotifyPackageActionsChanged();
    }

    private async void Environment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;
        var selected = listBox.SelectedItem as CondaEnvironment;
        if (ReferenceEquals(Browser.SelectedEnvironment, selected))
            return;
        await RunBrowserAsync(token => Browser.SelectEnvironmentAsync(selected, token));
    }

    private async void EnvironmentRefresh_Click(object sender, RoutedEventArgs e) =>
        await RunBrowserAsync(token => Browser.RefreshAsync(token));

    private async void PackageRefresh_Click(object sender, RoutedEventArgs e) =>
        await RunBrowserAsync(token => Browser.RefreshDetailsAsync(token));

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCreate || Discovery.SelectedInstallation is not { } installation)
            return;
        var dialog = new CreateEnvironmentDialog(installation) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Plan is { } plan)
            await CreateEnvironmentAsync(installation, plan);
    }

    private async Task CreateEnvironmentAsync(VerifiedInstallation installation, EnvironmentCreationPlan plan)
    {
        BeginOperation($"正在创建环境：{plan.Prefix}\n依赖求解和下载可能需要几分钟。请等待完成。");
        var created = false;
        var error = false;
        var cleanupUncertain = false;
        var result = string.Empty;
        try
        {
            await _operator.CreateAsync(installation, plan, _operationCancellation!.Token);
            created = true;
            result = $"环境创建成功：{plan.Prefix}";
            try
            {
                _managedPrefixes.Add(plan.Prefix);
            }
            catch (Exception exception)
            {
                error = true;
                result += $"\n但无法保存自定义环境路径：{exception.Message}";
            }
        }
        catch (OperationCanceledException exception) when (_operationCancellation?.IsCancellationRequested == true)
        {
            error = true;
            cleanupUncertain = HasUncertainProcessCleanup(exception);
            result = cleanupUncertain
                ? UncertainCleanupMessage(plan.Prefix)
                : $"已取消创建。目标目录可能不完整，请检查：{plan.Prefix}";
        }
        catch (Exception exception)
        {
            error = true;
            cleanupUncertain = HasUncertainProcessCleanup(exception);
            result = cleanupUncertain
                ? UncertainCleanupMessage(plan.Prefix)
                : $"创建失败：{exception.Message}\n请检查目标目录：{plan.Prefix}";
        }
        finally
        {
            CanCancelOperation = false;
            if (cleanupUncertain)
            {
                RequireOperationInspection();
            }
            else
            {
                if (!created && File.Exists(Path.Combine(plan.Prefix, "conda-meta", "history")))
                {
                    try
                    {
                        _managedPrefixes.Add(plan.Prefix);
                        result += "\n检测到残留的 Conda 环境，已加入列表供检查。";
                    }
                    catch (Exception exception)
                    {
                        result += $"\n无法记录残留环境路径：{exception.Message}";
                    }
                }
                OperationStatusMessage = "正在刷新环境列表和详情…";
                var refreshWarning = await RefreshAfterOperationAsync(created ? plan.Prefix : null);
                if (refreshWarning is not null)
                {
                    error = true;
                    result += $"\n{refreshWarning}";
                }
            }
            EndOperation(result, error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDelete || Discovery.SelectedInstallation is not { } installation ||
            Browser.SelectedEnvironment is not { } selected)
            return;

        var answer = MessageBox.Show(this,
            $"将永久删除以下环境目录及其中的所有文件：\n\n{selected.Prefix}\n\n此操作不可撤销。确定继续吗？",
            "确认删除 Conda 环境", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || !CanDelete)
            return;

        var protectedRoots = Discovery.Installations.Select(item => item.Info.RootPrefix).ToArray();
        var activePrefix = Environment.GetEnvironmentVariable("CONDA_PREFIX");
        await DeleteEnvironmentAsync(installation, selected.Prefix, protectedRoots, activePrefix);
    }

    private async Task DeleteEnvironmentAsync(VerifiedInstallation installation, string prefix,
        IReadOnlyCollection<string> protectedRoots, string? activePrefix)
    {
        BeginOperation($"正在删除环境：{prefix}\n请等待 Conda 完成清理。");
        var error = false;
        var cleanupUncertain = false;
        var result = string.Empty;
        try
        {
            await _operator.DeleteAsync(installation, prefix, protectedRoots, activePrefix,
                _operationCancellation!.Token);
            result = $"环境删除成功：{prefix}";
            try
            {
                _managedPrefixes.Remove(prefix);
            }
            catch (Exception exception)
            {
                error = true;
                result += $"\n但无法更新已保存的环境路径：{exception.Message}";
            }
        }
        catch (OperationCanceledException exception) when (_operationCancellation?.IsCancellationRequested == true)
        {
            error = true;
            cleanupUncertain = HasUncertainProcessCleanup(exception);
            result = cleanupUncertain
                ? UncertainCleanupMessage(prefix)
                : $"已取消删除。请检查环境目录是否仍存在：{prefix}";
        }
        catch (Exception exception)
        {
            error = true;
            cleanupUncertain = HasUncertainProcessCleanup(exception);
            result = cleanupUncertain
                ? UncertainCleanupMessage(prefix)
                : $"删除失败：{exception.Message}\n请检查环境目录：{prefix}";
        }
        finally
        {
            CanCancelOperation = false;
            if (cleanupUncertain)
            {
                RequireOperationInspection();
            }
            else
            {
                OperationStatusMessage = "正在刷新环境列表和详情…";
                var refreshWarning = await RefreshAfterOperationAsync(null);
                if (refreshWarning is not null)
                {
                    error = true;
                    result += $"\n{refreshWarning}";
                }
            }
            EndOperation(result, error);
        }
    }

    private async Task<string?> RefreshAfterOperationAsync(string? preferredPrefix)
    {
        try
        {
            if (preferredPrefix is null)
                await Browser.RefreshAsync(_windowCancellation.Token);
            else
                await Browser.RefreshAsync(preferredPrefix, _windowCancellation.Token);
            return Browser.EnvironmentState == BrowserLoadState.Error
                ? $"环境列表刷新失败：{Browser.EnvironmentError}"
                : null;
        }
        catch (Exception exception)
        {
            return $"环境列表刷新失败：{exception.Message}";
        }
    }

    private void BeginOperation(string message)
    {
        _operationCancellation = new CancellationTokenSource();
        IsOperationError = false;
        OperationStatusMessage = message;
        IsOperating = true;
        CanCancelOperation = true;
    }

    private void EndOperation(string message, bool isError)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        CanCancelOperation = false;
        IsOperating = false;
        IsOperationError = isError;
        OperationStatusMessage = message;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCancelOperation)
            return;
        CanCancelOperation = false;
        OperationStatusMessage = "正在取消 Conda 操作并等待进程退出…";
        _operationCancellation?.Cancel();
    }

    private static bool HasUncertainProcessCleanup(Exception exception) =>
        exception.Message.Contains("Cleanup failed:", StringComparison.OrdinalIgnoreCase);

    private static string UncertainCleanupMessage(string prefix) =>
        $"无法确认 Conda 进程已经退出。请在任务管理器确认进程结束，检查目标目录，然后重启应用：{prefix}";

    private void RequireOperationInspection()
    {
        _operationNeedsInspection = true;
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanDelete));
        NotifyPackageActionsChanged();
    }

    private static bool IsSameOrAncestor(string candidate, string protectedRoot)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        var boundary = normalizedCandidate + (Path.EndsInDirectorySeparator(normalizedCandidate)
            ? string.Empty : Path.DirectorySeparatorChar.ToString());
        return normalizedRoot.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async Task RunBrowserAsync(Func<CancellationToken, Task> action)
    {
        if (_windowCancellation.IsCancellationRequested)
            return;
        try
        {
            await action(_windowCancellation.Token);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_windowCancellation.IsCancellationRequested)
                MessageBox.Show(this, exception.Message, "读取环境失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsOperating)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Conda 操作仍在进行。请先等待完成，或点击“取消操作”并等待清理结束。",
                "操作尚未结束", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowCancellation.Cancel();
        Discovery.PropertyChanged -= Discovery_PropertyChanged;
        Browser.PropertyChanged -= Browser_PropertyChanged;
        Browser.Dispose();
        base.OnClosed(e);
    }
}
