using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;

namespace CondaManager.App;

public partial class InstallPackageDialog : Window
{
    public InstallPackageDialog(string prefix)
    {
        InitializeComponent();
        TargetTextBlock.Text = prefix;
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string? PackageName { get; private set; }
    public string? PackageVersion { get; private set; }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewButton is not null)
            UpdateSpecPreview();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateSpecPreview())
            return;

        PackageName = NameTextBox.Text;
        PackageVersion = VersionTextBox.Text.Length == 0 ? null : VersionTextBox.Text;
        DialogResult = true;
    }

    private bool UpdateSpecPreview()
    {
        PreviewButton.IsEnabled = false;
        SpecTextBlock.Text = "—";
        ValidationTextBlock.Text = string.Empty;
        if (NameTextBox.Text.Length == 0)
            return false;

        try
        {
            var version = VersionTextBox.Text.Length == 0 ? null : VersionTextBox.Text;
            SpecTextBlock.Text = CondaPackageOperator.BuildInstallSpec(NameTextBox.Text, version);
            PreviewButton.IsEnabled = true;
            return true;
        }
        catch (ArgumentException exception)
        {
            ValidationTextBlock.Text = exception.ParamName == "version"
                ? "请输入简单的版本前缀，例如 1.26。"
                : "软件包名称不符合上述格式。";
            return false;
        }
    }
}
