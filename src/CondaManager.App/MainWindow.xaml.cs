using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;
using Microsoft.Win32;

namespace CondaManager.App;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _windowCancellation = new();

    public MainViewModel Discovery { get; }
    public EnvironmentBrowserViewModel Browser { get; }

    public MainWindow()
    {
        var runner = new ProcessRunner();
        var settings = new SelectionSettings();
        var discovery = new CondaDiscoveryService(new CondaLauncherValidator(runner));
        Discovery = new MainViewModel(discovery, settings, savedRoot =>
            CondaCandidateFinder.FindRoots(
                savedRoot,
                Environment.GetEnvironmentVariable("CONDA_EXE"),
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        Browser = new EnvironmentBrowserViewModel(new CondaEnvironmentCatalog(runner));
        Discovery.PropertyChanged += Discovery_PropertyChanged;
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
        if (e.PropertyName == nameof(MainViewModel.SelectedInstallation))
            await RunBrowserAsync(token => Browser.SetInstallationAsync(Discovery.SelectedInstallation, token));
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

    protected override void OnClosed(EventArgs e)
    {
        _windowCancellation.Cancel();
        Discovery.PropertyChanged -= Discovery_PropertyChanged;
        Browser.Dispose();
        base.OnClosed(e);
    }
}
