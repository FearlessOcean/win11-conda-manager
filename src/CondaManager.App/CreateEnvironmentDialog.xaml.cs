using System.IO;
using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;
using Microsoft.Win32;

namespace CondaManager.App;

public partial class CreateEnvironmentDialog : Window
{
    private EnvironmentCreationPlan? _validatedPlan;
    private bool _initialized;

    public CreateEnvironmentDialog(VerifiedInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        InitializeComponent();

        InstallationTextBlock.Text = installation.Info.RootPrefix;
        var defaultParent = EnvironmentCreationPlanner.ChooseDefaultParent(
            installation.Info, new WritableDirectoryProbe());
        ParentTextBox.Text = defaultParent ?? string.Empty;
        DefaultHintTextBlock.Text = defaultParent is null
            ? "未找到可写的默认环境目录。请选择或输入一个存放目录。"
            : "已预填该 Conda 安装的首个可写环境目录。可在此修改本次创建的位置。";

        _initialized = true;
        UpdatePreview();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public EnvironmentCreationPlan? Plan { get; private set; }

    private void Input_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择虚拟环境存放目录" };
        if (dialog.ShowDialog(this) == true)
            ParentTextBox.Text = dialog.FolderName;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (_validatedPlan is null)
            return;

        Plan = _validatedPlan;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdatePreview()
    {
        if (!_initialized)
            return;

        _validatedPlan = null;
        CreateButton.IsEnabled = false;
        try
        {
            var plan = EnvironmentCreationPlanner.Build(
                NameTextBox.Text, ParentTextBox.Text, VersionTextBox.Text);
            PreviewTextBlock.Text = plan.Prefix;
            if (Directory.Exists(plan.Prefix) || File.Exists(plan.Prefix))
            {
                ValidationTextBlock.Text = "目标路径已经存在。请更换环境名称或存放目录。";
                return;
            }

            ValidationTextBlock.Text = string.Empty;
            _validatedPlan = plan;
            CreateButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            PreviewTextBlock.Text = "请输入有效的名称和绝对存放路径";
            ValidationTextBlock.Text = exception.Message;
        }
    }
}
