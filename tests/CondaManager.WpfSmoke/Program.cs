using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Reflection;
using CondaManager.App;
using CondaManager.Core;

internal static class Program
{
    private static readonly VerifiedInstallation Installation = new(new CondaInfo(@"C:\fake", "25", []), new CondaLauncher(@"C:\fake\python.exe", ["-m", "conda"]));
    private static readonly AvailablePackage Package = new("_numpy", "1!1.26+local", "py312_0", "defaults", "win-64");
    private static readonly FakeSearch Search = new();
    private static readonly FakeStore Store = new();

    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var create = new CreateEnvironmentDialog(Installation);
        Show(create, create.Close);
        var channels = new ChannelManagerDialog(Installation, ChannelSnapshot.Inherit, Search, Store);
        Show(channels, () =>
        {
            Click(channels, "ReadButton");
            Check(Get<TextBlock>(channels, "ConfiguredText").Text.Contains("defaults"), "configured channels");
            Get<RadioButton>(channels, "CustomMode").IsChecked = true;
            Add(channels, "http://example.test/private");
            Check(!Get<Button>(channels, "SaveButton").IsEnabled && Get<ListBox>(channels, "ChannelList").Items.Count == 0, "unsafe channel rejected");
            Add(channels, "defaults");
            Add(channels, "conda-forge");
            Click(channels, "UpButton");
            Click(channels, "SaveButton");
        });
        Check(Store.Saved.Channels.SequenceEqual(new[] {"conda-forge", "defaults"}), "saved channel order");
        var caseEditor = new ChannelManagerDialog(Installation, Store.Saved, Search, Store);
        Show(caseEditor, () =>
        {
            Add(caseEditor, "https://repo.example.org/Channels/Main");
            Add(caseEditor, "https://repo.example.org/channels/main");
            Check(Get<ListBox>(caseEditor, "ChannelList").Items.Count == 4,
                "distinct case-sensitive URL paths remain separate");
            caseEditor.Close();
        });
        var canceledEditor = new ChannelManagerDialog(Installation, Store.Saved, Search, Store);
        Show(canceledEditor, () =>
        {
            Get<ListBox>(canceledEditor, "ChannelList").SelectedIndex = 0;
            Click(canceledEditor, "RemoveButton");
            Click(canceledEditor, "RemoveButton");
            Check(Get<RadioButton>(canceledEditor, "InheritMode").IsChecked == true, "empty custom list inherits");
            canceledEditor.Close();
        });
        Check(Store.Saved.IsOverride, "cancel preserves stored settings");

        var search = new PackageSearchDialog(Installation, Store.Saved, Search, true);
        Show(search, () =>
        {
            Get<TextBox>(search, "QueryText").Text = "num";
            Check(Search.Count == 0, "explicit search");
            Click(search, "SearchButton");
            Check(Search.Count == 1 && Search.LastChannels == Store.Saved, "channel snapshot");
            Get<DataGrid>(search, "ResultGrid").SelectedIndex = 0;
            Check(Get<Button>(search, "InstallButton").IsEnabled, "install selection");
            Click(search, "InstallButton");
        });
        Check(search.ChosenPackage == Package, "selection result");
        var install = new InstallPackageDialog(@"C:\fake\envs\demo", search.ChosenPackage);
        Show(install, () =>
        {
            Check(Get<TextBox>(install, "NameTextBox").Text == Package.Name, "name");
            Check(Get<TextBox>(install, "VersionTextBox").Text == Package.Version, "version");
            Check(install.ExactVersion && Get<TextBlock>(install, "SpecTextBlock").Text == "_numpy==1!1.26+local", "exact release, unpinned build");
            Get<TextBox>(install, "NameTextBox").Text = "numpy";
            Get<TextBox>(install, "VersionTextBox").Text = "1.26";
            Check(!install.ExactVersion && Get<TextBlock>(install, "SpecTextBlock").Text == "numpy=1.26", "edited prefix");
            Get<TextBox>(install, "NameTextBox").Text = Package.Name;
            Get<TextBox>(install, "VersionTextBox").Text = Package.Version;
            Check(Get<Button>(install, "PreviewButton").IsEnabled, "exact selection can preview");
            Click(install, "PreviewButton");
        });
        Check(install.ExactVersion && install.PackageName == Package.Name && install.PackageVersion == Package.Version, "exact request fields");
        var request = new PackageOperationRequest(Installation, @"C:\fake\envs\demo", PackageAction.Install,
            install.PackageName!, install.PackageVersion, null, []) { ExactVersion = install.ExactVersion };
        var preview = new PackagePreviewDialog(new PackagePreview(request, [], [], []));
        Show(preview, () =>
        {
            Check(Get<TextBlock>(preview, "ActionTextBlock").Text == "安装 _numpy==1!1.26+local", "exact transaction confirmation");
            preview.Close();
        });
        var readOnly = new PackageSearchDialog(Installation, Store.Saved, Search, false);
        Show(readOnly, () =>
        {
            Get<TextBox>(readOnly, "QueryText").Text = "num";
            Click(readOnly, "SearchButton");
            Get<DataGrid>(readOnly, "ResultGrid").SelectedIndex = 0;
            Check(!Get<Button>(readOnly, "InstallButton").IsEnabled, "read only");
            Search.Pending = true;
            Click(readOnly, "SearchButton");
            Check(Get<Button>(readOnly, "CancelSearchButton").IsEnabled, "cancel enabled");
            Click(readOnly, "CancelSearchButton");
            Check(Search.LastToken.IsCancellationRequested, "cancel token");
            Click(readOnly, "SearchButton");
            readOnly.Close();
            Check(Search.LastToken.IsCancellationRequested, "close cancels");
        });
        TestLoadingEnvironmentTransition();
        app.Shutdown();
        Console.WriteLine("PASS WPF icon, fake channel management, repository search, base guard, install prefill, cancellation.");
    }

    private static void TestLoadingEnvironmentTransition()
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var main = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        Check(main.Icon is not null, "MainWindow icon");
        var loaded = typeof(MainWindow).GetMethod("Window_Loaded", hidden)!;
        main.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), main);
        typeof(MainViewModel).GetField("_selectedInstallation", hidden)!.SetValue(main.Discovery, Installation);
        typeof(MainWindow).GetField("_packageSearch", hidden)!.SetValue(main, new FakeSearch());
        typeof(MainWindow).GetField("_channels", hidden)!.SetValue(main, ChannelSnapshot.Custom(["defaults"]));
        typeof(MainWindow).GetField("_channelWarning", hidden)!.SetValue(main, "其他安装的配置异常。");
        Check(main.ChannelModeLabel.Contains("自定义渠道（1）") && main.ChannelModeLabel.Contains("其他安装的配置异常。"), "channel mode and profile warning visible");
        main.Show();
        var target = new CondaEnvironment(@"C:\fake\envs\later", "later", false);
        bool enabled = false, openedInstall = false;
        main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var search = main.OwnedWindows.OfType<PackageSearchDialog>().Single();
            Get<TextBox>(search, "QueryText").Text = "num";
            Click(search, "SearchButton");
            Get<DataGrid>(search, "ResultGrid").SelectedIndex = 0;
            Check(!Get<Button>(search, "InstallButton").IsEnabled, "missing environment disables install");
            var browser = main.Browser;
            typeof(EnvironmentBrowserViewModel).GetProperty("SelectedEnvironment")!.SetValue(browser, target);
            typeof(EnvironmentBrowserViewModel).GetProperty("EnvironmentState")!.SetValue(browser, BrowserLoadState.Ready);
            typeof(EnvironmentBrowserViewModel).GetProperty("PackageState")!.SetValue(browser, BrowserLoadState.Ready);
            enabled = Get<Button>(search, "InstallButton").IsEnabled;
            if (!enabled) { search.Close(); return; }
            main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var form = main.OwnedWindows.OfType<InstallPackageDialog>().Single();
                openedInstall = Get<TextBlock>(form, "TargetTextBlock").Text == target.Prefix && form.ExactVersion;
                form.Close();
            });
            Click(search, "InstallButton");
        });
        typeof(MainWindow).GetMethod("SearchPackages_Click", hidden)!.Invoke(main, [main, new RoutedEventArgs()]);
        main.Close();
        Check(enabled && openedInstall, "environment loaded inside search must enable install and open current target");
    }

    private static void Add(Window window, string value)
    {
        Get<TextBox>(window, "EntryText").Text = value;
        var panel = (DockPanel)Get<TextBox>(window, "EntryText").Parent;
        panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Show(Window dialog, Action test)
    {
        Check(dialog.Icon is not null, $"{dialog.GetType().Name} icon");
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.ShowInTaskbar = false;
        dialog.Left = -20000;
        dialog.Top = -20000;
        Exception? error = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try { test(); }
            catch (Exception ex) { error = ex; dialog.Close(); }
        });
        dialog.ShowDialog();
        if (error is not null) throw error;
    }

    private static T Get<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(Window window, string name) =>
        Get<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Check(bool value, string condition)
    {
        if (!value) throw new InvalidOperationException(condition);
    }

    private sealed class FakeStore : IChannelProfileStore
    {
        public ChannelSnapshot Saved = ChannelSnapshot.Inherit;
        public ChannelProfileLoadResult Load(string root) => new(Saved, null);
        public void Save(string root, ChannelSnapshot snapshot) => Saved = snapshot;
    }
    private sealed class FakeSearch : ICondaPackageSearch
    {
        public int Count;
        public bool Pending;
        public CancellationToken LastToken;
        public ChannelSnapshot? LastChannels;
        public async Task<PackageSearchPage> SearchAsync(VerifiedInstallation installation, string query, ChannelSnapshot channels, CancellationToken token)
        {
            Count++;
            LastChannels = channels;
            LastToken = token;
            if (Pending) await Task.Delay(Timeout.Infinite, token);
            return new PackageSearchPage([Package], false);
        }
        public Task<IReadOnlyList<string>> GetConfiguredChannelsAsync(VerifiedInstallation installation, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>(["defaults"]);
    }
}
