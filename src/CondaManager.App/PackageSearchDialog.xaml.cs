using System.ComponentModel;
using System.Windows;
using CondaManager.Core;

namespace CondaManager.App;

public partial class PackageSearchDialog : Window
{
    private readonly VerifiedInstallation _installation;
    private readonly ChannelSnapshot _channels;
    private readonly AvailablePackageSearchViewModel _model;
    private bool _canInstall;

    public PackageSearchDialog(VerifiedInstallation installation, ChannelSnapshot channels,
        ICondaPackageSearch search, bool canInstall)
    {
        _installation = installation;
        _channels = channels;
        _model = new(search);
        InitializeComponent();
        DataContext = _model;
        ChannelText.Text = channels.IsOverride ? $"搜索渠道：应用自定义（{channels.Channels.Count}）" : "搜索渠道：沿用 Conda 当前渠道";
        SetInstallEligibility(canInstall);
        _model.PropertyChanged += Model_Changed;
        Loaded += (_, _) => QueryText.Focus();
    }

    public AvailablePackage? ChosenPackage { get; private set; }

    internal void SetInstallEligibility(bool canInstall)
    {
        _canInstall = canInstall;
        TargetHint.Text = canInstall ? "选择结果可预填安装表单。" : "可以浏览结果。安装需要选择可写的非 base 环境。";
        UpdateActions();
    }

    private void Model_Changed(object? sender, PropertyChangedEventArgs e) => UpdateActions();

    private void UpdateActions()
    {
        var valid = CondaPackageSearch.IsValidQuery(_model.Query);
        SearchButton.IsEnabled = valid && !_model.IsSearching;
        InstallButton.IsEnabled = _canInstall && _model.State == BrowserLoadState.Ready && _model.SelectedResult is not null;
        StatusText.Text = _model.State switch
        {
            BrowserLoadState.Loading => "正在搜索…",
            BrowserLoadState.Error => _model.Error ?? "搜索失败。",
            BrowserLoadState.Empty => "没有找到匹配的软件包。",
            BrowserLoadState.Ready when _model.Truncated => $"显示前 {_model.Items.Count} 条结果。请缩小搜索范围。",
            BrowserLoadState.Ready => $"找到 {_model.Items.Count} 条结果。",
            _ => "以英文字母或数字开头，可包含下划线、连字符和句点。"
        };
    }

    private async void Search_Click(object sender, RoutedEventArgs e) =>
        await _model.SearchAsync(_installation, _channels);

    private void CancelSearch_Click(object sender, RoutedEventArgs e) => _model.Cancel();

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_canInstall || _model.State != BrowserLoadState.Ready || _model.SelectedResult is not { } selected)
            return;
        ChosenPackage = selected;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.PropertyChanged -= Model_Changed;
        _model.Dispose();
        base.OnClosed(e);
    }
}
