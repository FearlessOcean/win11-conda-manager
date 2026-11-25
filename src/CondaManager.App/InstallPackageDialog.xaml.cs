using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;

namespace CondaManager.App;

public partial class InstallPackageDialog : Window
{
    private readonly AvailablePackage? _selected;

    public InstallPackageDialog(string prefix, AvailablePackage? selected = null)
    {
        _selected = selected;
        InitializeComponent();
        TargetTextBlock.Text = prefix;
        if (selected is not null)
        {
            NameTextBox.Text = selected.Name;
            VersionTextBox.Text = selected.Version;
        }
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string? PackageName { get; private set; }
    public string? PackageVersion { get; private set; }
    public bool ExactVersion => _selected is not null && NameTextBox.Text == _selected.Name &&
        VersionTextBox.Text == _selected.Version;

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
        NameHint.Text = ExactVersion ? "名称来自选中的搜索结果。" :
            "最多 128 个字符。以英文字母、数字或下划线开头，可包含下划线、连字符和句点。";
        VersionHint.Text = ExactVersion ? "精确匹配搜索结果的版本。修改名称或版本后按手动输入的版本前缀匹配。" :
            "手动输入按版本前缀匹配，例如 1.26。留空时由 Conda 选择版本。";
        if (NameTextBox.Text.Length == 0)
            return false;

        try
        {
            var version = VersionTextBox.Text.Length == 0 ? null : VersionTextBox.Text;
            SpecTextBlock.Text = CondaPackageOperator.BuildInstallSpec(NameTextBox.Text, version, ExactVersion);
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
