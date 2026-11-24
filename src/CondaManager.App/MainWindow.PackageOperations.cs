using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;

namespace CondaManager.App;

public partial class MainWindow
{
    private readonly ICondaPackageOperator _packageOperator;

    public bool CanInstallPackage => CanInteract && IsPackageTargetEligible;

    public bool CanChangeSelectedPackage => CanInstallPackage && SelectedCondaPackage is not null;

    private bool IsPackageTargetEligible =>
        !_operationNeedsInspection && !Discovery.IsScanning &&
        Discovery.SelectedInstallation is not null &&
        Browser.EnvironmentState == BrowserLoadState.Ready &&
        Browser.PackageState is BrowserLoadState.Ready or BrowserLoadState.Empty &&
        Browser.SelectedEnvironment is { IsBase: false } target &&
        !Discovery.Installations.Any(installation =>
            string.Equals(installation.Info.RootPrefix, target.Prefix, StringComparison.OrdinalIgnoreCase));

    private CondaPackage? SelectedCondaPackage =>
        PackageGrid?.SelectedItem is CondaPackage { Origin: CondaPackageOrigin.Conda } package &&
        Browser.VisiblePackages.Any(item => ReferenceEquals(item, package)) ? package : null;

    private void NotifyPackageActionsChanged()
    {
        OnPropertyChanged(nameof(CanInstallPackage));
        OnPropertyChanged(nameof(CanChangeSelectedPackage));
    }

    private void PackageSelection_Changed(object sender, SelectionChangedEventArgs e) =>
        NotifyPackageActionsChanged();

    private async void InstallPackage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInstallPackage || Discovery.SelectedInstallation is not { } installation ||
            Browser.SelectedEnvironment is not { } environment)
            return;

        var dialog = new InstallPackageDialog(environment.Prefix) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.PackageName is not { } packageName)
            return;

        var request = new PackageOperationRequest(installation, environment.Prefix, PackageAction.Install,
            packageName, dialog.PackageVersion, null, GetPackageProtectedRoots());
        if (CanInstallPackage && IsCurrentPackageRequest(request, environment))
            await RunPackageOperationAsync(request, environment);
    }

    private async void UpdatePackage_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedPackageOperationAsync(PackageAction.Update);

    private async void RemovePackage_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedPackageOperationAsync(PackageAction.Remove);

    private async Task RunSelectedPackageOperationAsync(PackageAction action)
    {
        if (!CanInteract || !IsPackageTargetEligible || SelectedCondaPackage is not { } package ||
            Discovery.SelectedInstallation is not { } installation ||
            Browser.SelectedEnvironment is not { } environment)
            return;

        var request = new PackageOperationRequest(installation, environment.Prefix, action,
            package.Name, null, package, GetPackageProtectedRoots());
        await RunPackageOperationAsync(request, environment);
    }

    private string[] GetPackageProtectedRoots() =>
        Discovery.Installations.Select(item => item.Info.RootPrefix).ToArray();

    private bool IsCurrentPackageRequest(PackageOperationRequest request, CondaEnvironment environment) =>
        IsPackageTargetEligible &&
        ReferenceEquals(Discovery.SelectedInstallation, request.Installation) &&
        ReferenceEquals(Browser.SelectedEnvironment, environment) &&
        string.Equals(environment.Prefix, request.Prefix, StringComparison.OrdinalIgnoreCase) &&
        request.ProtectedRoots.SequenceEqual(GetPackageProtectedRoots(), StringComparer.OrdinalIgnoreCase) &&
        (request.Action == PackageAction.Install ||
            ReferenceEquals(SelectedCondaPackage, request.InstalledPackage));

    private async Task RunPackageOperationAsync(PackageOperationRequest request, CondaEnvironment environment)
    {
        var action = PackagePreviewDialog.GetActionLabel(request.Action);
        BeginOperation($"正在预览{action} {request.PackageName}\n目标环境：{request.Prefix}");
        var token = _operationCancellation!.Token;
        var attemptedApply = false;
        var cleanupUncertain = false;
        var error = false;
        var result = string.Empty;
        try
        {
            var preview = await _packageOperator.PreviewAsync(request, token);
            token.ThrowIfCancellationRequested();
            CanCancelOperation = false;

            var dialog = new PackagePreviewDialog(preview) { Owner = this };
            var confirmed = dialog.ShowDialog() == true;
            if (!preview.HasChanges)
            {
                result = "Conda 预览未发现需要执行的软件包变更。";
                return;
            }

            if (!confirmed)
            {
                result = $"已取消{action} {request.PackageName}。";
                return;
            }

            if (!IsCurrentPackageRequest(request, environment))
            {
                error = true;
                result = "目标环境或软件包选择已变化，请重新预览。";
                return;
            }

            OperationStatusMessage = $"正在{action} {request.PackageName}\n目标环境：{request.Prefix}";
            CanCancelOperation = true;
            attemptedApply = true;
            await _packageOperator.ApplyAsync(request, preview, token);
            result = $"已完成{action} {request.PackageName}。";
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested)
        {
            error = true;
            cleanupUncertain = HasUncertainProcessCleanup(exception);
            result = cleanupUncertain
                ? UncertainCleanupMessage(request.Prefix)
                : $"已取消{action} {request.PackageName}。";
        }
        catch (Exception exception)
        {
            error = true;
            cleanupUncertain = HasUncertainProcessCleanup(exception);
            result = cleanupUncertain
                ? UncertainCleanupMessage(request.Prefix)
                : $"{(attemptedApply ? action : "预览")}失败。";
            if (!cleanupUncertain && exception is CondaOperationException)
                result += $" {exception.Message}";
        }
        finally
        {
            CanCancelOperation = false;
            if (cleanupUncertain)
            {
                RequireOperationInspection();
            }
            else if (attemptedApply)
            {
                OperationStatusMessage = "正在刷新软件包列表…";
                if (!await RefreshPackageDetailsAfterOperationAsync())
                {
                    error = true;
                    result += "\n软件包列表刷新失败。";
                }
            }
            if (!cleanupUncertain)
                result += $"\n目标环境：{request.Prefix}";
            EndOperation(result, error);
        }
    }

    private async Task<bool> RefreshPackageDetailsAfterOperationAsync()
    {
        try
        {
            await Browser.RefreshDetailsAsync(_windowCancellation.Token);
            return Browser.PackageState is BrowserLoadState.Ready or BrowserLoadState.Empty;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
