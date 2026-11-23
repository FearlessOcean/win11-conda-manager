using System.Windows;
using CondaManager.Core;
using Microsoft.Win32;

namespace CondaManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _windowCancellation = new();

    public MainWindow()
    {
        InitializeComponent();

        var settings = new SelectionSettings();
        var discovery = new CondaDiscoveryService(new CondaLauncherValidator(new ProcessRunner()));
        _viewModel = new MainViewModel(discovery, settings, savedRoot =>
            CondaCandidateFinder.FindRoots(
                savedRoot,
                Environment.GetEnvironmentVariable("CONDA_EXE"),
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        DataContext = _viewModel;
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
            await _viewModel.AddManualRootAsync(dialog.FolderName, _windowCancellation.Token);
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
            await _viewModel.ScanAsync(_windowCancellation.Token);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "扫描 Conda 安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowCancellation.Cancel();
        base.OnClosed(e);
    }
}
