using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CondaManager.Core;

namespace CondaManager.App;

public partial class ChannelManagerDialog : Window
{
    private readonly VerifiedInstallation _installation;
    private readonly IChannelProfileStore _store;
    private readonly ICondaPackageSearch _search;
    private readonly ObservableCollection<string> _entries;
    private readonly CancellationTokenSource _lifetime = new();

    public ChannelManagerDialog(VerifiedInstallation installation, ChannelSnapshot current,
        ICondaPackageSearch search, IChannelProfileStore store)
    {
        _installation = installation;
        _store = store;
        _search = search;
        _entries = new(current.Channels);
        InitializeComponent();
        RootText.Text = installation.Info.RootPrefix;
        ChannelList.ItemsSource = _entries;
        (current.IsOverride ? CustomMode : InheritMode).IsChecked = true;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var index = ChannelList.SelectedIndex;
        RemoveButton.IsEnabled = index >= 0;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index + 1 < _entries.Count;
        SaveButton.IsEnabled = CustomMode.IsChecked != true ||
            (_entries.Count > 0 && EntryText.Text.Length == 0);
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        UpdateButtons();
    }

    private void Entry_Changed(object sender, TextChangedEventArgs e) => UpdateButtons();

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        ReadButton.IsEnabled = false;
        ConfiguredText.Text = "正在读取…";
        var token = _lifetime.Token;
        try
        {
            var labels = await _search.GetConfiguredChannelsAsync(_installation, token);
            if (!token.IsCancellationRequested)
                ConfiguredText.Text = labels.Count == 0 ? "没有可显示的渠道。" : string.Join("、", labels);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                ConfiguredText.Text = "读取 Conda 渠道失败，仍可沿用当前配置。";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                ReadButton.IsEnabled = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var value = ChannelPolicy.Normalize(EntryText.Text);
            var existing = _entries.FirstOrDefault(entry => ChannelPolicy.NormalizedEntryComparer.Equals(entry, value));
            if (existing is null)
                _entries.Add(value);
            ChannelList.SelectedItem = existing ?? value;
            EntryText.Clear();
            ValidationText.Text = existing is null ? string.Empty : "列表已包含此渠道。";
        }
        catch (ArgumentException)
        {
            ValidationText.Text = "请输入有效的渠道名称或 HTTPS 地址。地址不能包含凭据、空白、查询参数或片段。";
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is not string entry)
            return;
        var index = ChannelList.SelectedIndex;
        _entries.Remove(entry);
        if (_entries.Count == 0)
            InheritMode.IsChecked = true;
        else
            ChannelList.SelectedIndex = Math.Min(index, _entries.Count - 1);
        UpdateButtons();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        var index = ChannelList.SelectedIndex;
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= _entries.Count)
            return;
        _entries.Move(index, destination);
        ChannelList.SelectedIndex = destination;
        UpdateButtons();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;
        var snapshot = CustomMode.IsChecked == true ? ChannelSnapshot.Custom(_entries) : ChannelSnapshot.Inherit;
        try
        {
            _store.Save(_installation.Info.RootPrefix, snapshot);
        }
        catch (Exception)
        {
            ValidationText.Text = "保存渠道设置失败。请检查设置文件的写入权限后重试。";
            return;
        }
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }
}
