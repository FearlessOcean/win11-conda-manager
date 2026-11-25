using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;

namespace CondaManager.App;

public partial class PackagePreviewDialog : Window
{
    public PackagePreviewDialog(PackagePreview preview)
    {
        InitializeComponent();
        var request = preview.Request;
        var spec = request.Action == PackageAction.Install
            ? CondaPackageOperator.BuildInstallSpec(request.PackageName, request.Version, request.ExactVersion)
            : request.PackageName;
        ActionTextBlock.Text = $"{GetActionLabel(request.Action)} {spec}";
        TargetTextBlock.Text = request.Prefix;

        if (!preview.HasChanges)
        {
            PlanTextBlock.Text = "Conda 预览未发现需要执行的软件包变更。";
            ConfirmButton.Visibility = Visibility.Collapsed;
            ChangesScrollViewer.Visibility = Visibility.Collapsed;
            CancelButton.Content = "关闭";
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            return;
        }

        PlanTextBlock.Text = "此操作可能同时更改其他依赖项。确认后 Conda 会重新求解，实际变更可能与预览不同。";
        AddGroup("将安装或替换", preview.ToInstall);
        AddGroup("将移除（包括被替换的版本）", preview.ToRemove);
        AddGroup("将下载", preview.ToFetch);
    }

    internal static string GetActionLabel(PackageAction action) => action switch
    {
        PackageAction.Install => "安装",
        PackageAction.Update => "更新",
        PackageAction.Remove => "卸载",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void AddGroup(string title, IEnumerable<PackageChange> changes)
    {
        var rows = changes.Select(change =>
            string.Join("  ", new[] { change.Name, change.Version, change.Build }
                .Where(value => !string.IsNullOrEmpty(value)))).ToArray();
        if (rows.Length == 0)
            return;

        PlanGroups.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        PlanGroups.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, rows),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 0, 14)
        });
    }
}
