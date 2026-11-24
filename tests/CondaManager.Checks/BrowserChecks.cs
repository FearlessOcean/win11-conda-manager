using CondaManager.Core;

namespace CondaManager.Checks;

public static class BrowserChecks
{
    [Check("browser loads the selected environment and derives Python from packages")]
    public static async Task InitialLoad()
    {
        var root = Root("install");
        var other = Root("install", "envs", "science");
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => Task.FromResult<IReadOnlyList<CondaEnvironment>>(
                [Environment(root, true), Environment(other)]),
            Packages = (_, prefix, _) => Task.FromResult<IReadOnlyList<CondaPackage>>(
                prefix == root
                    ? [new("python", "3.12.4", "0", "conda-forge"), new("numpy", "2.1", null, null)]
                    : [])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);

        await browser.SetInstallationAsync(Installation(root));

        Expect.Equal(BrowserLoadState.Ready, browser.EnvironmentState);
        Expect.Equal(BrowserLoadState.Ready, browser.PackageState);
        Expect.Equal(root, browser.SelectedEnvironment!.Prefix);
        Expect.Equal("3.12.4", browser.PythonVersion);
        Expect.Equal(2, browser.TotalPackageCount);
        Expect.Equal(1, catalog.PackageCalls.Count);
        Expect.Equal(root, catalog.PackageCalls[0]);
    }

    [Check("switching environments rejects a late package response")]
    public static async Task StalePackagesCannotReplaceCurrentSelection()
    {
        var root = Root("install");
        var other = Root("install", "envs", "science");
        var delayed = new TaskCompletionSource<IReadOnlyList<CondaPackage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => Task.FromResult<IReadOnlyList<CondaEnvironment>>(
                [Environment(root, true), Environment(other)]),
            Packages = (_, prefix, _) => prefix == root
                ? delayed.Task
                : Task.FromResult<IReadOnlyList<CondaPackage>>([new("scipy", "1.16", null, null)])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);

        var firstLoad = browser.SetInstallationAsync(Installation(root));
        Expect.Equal(BrowserLoadState.Loading, browser.PackageState);
        await browser.SelectEnvironmentAsync(Environment(other));
        delayed.SetResult([new("python", "0.0-old", null, null)]);
        await firstLoad;

        Expect.Equal(other, browser.SelectedEnvironment!.Prefix);
        Expect.Equal(1, browser.VisiblePackages.Count);
        Expect.Equal("scipy", browser.VisiblePackages[0].Name);
        Expect.Equal("未安装 Python", browser.PythonVersion);
    }

    [Check("switching installations rejects a late environment response")]
    public static async Task StaleEnvironmentsCannotReplaceCurrentInstallation()
    {
        var first = Root("first");
        var second = Root("second");
        var delayed = new TaskCompletionSource<IReadOnlyList<CondaEnvironment>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog
        {
            Environments = (installation, _) => installation.Info.RootPrefix == first
                ? delayed.Task
                : Task.FromResult<IReadOnlyList<CondaEnvironment>>([Environment(second, true)]),
            Packages = (_, _, _) => Task.FromResult<IReadOnlyList<CondaPackage>>([])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);

        var oldLoad = browser.SetInstallationAsync(Installation(first));
        await browser.SetInstallationAsync(Installation(second));
        delayed.SetResult([Environment(first, true)]);
        await oldLoad;

        Expect.Equal(1, browser.Environments.Count);
        Expect.Equal(second, browser.Environments[0].Prefix);
        Expect.Equal(second, browser.SelectedEnvironment!.Prefix);
        Expect.True(catalog.PackageCalls.All(prefix => prefix == second),
            "An obsolete installation must never start a package query.");
    }

    [Check("refresh preserves the selected prefix and falls back when removed")]
    public static async Task RefreshSelection()
    {
        var root = Root("install");
        var other = Root("install", "envs", "science");
        var includeOther = true;
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => Task.FromResult<IReadOnlyList<CondaEnvironment>>(
                includeOther ? [Environment(root, true), Environment(other)] : [Environment(root, true)]),
            Packages = (_, _, _) => Task.FromResult<IReadOnlyList<CondaPackage>>([])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);
        await browser.SetInstallationAsync(Installation(root));
        await browser.SelectEnvironmentAsync(browser.Environments[1]);

        await browser.RefreshAsync();
        Expect.Equal(other, browser.SelectedEnvironment!.Prefix);
        includeOther = false;
        await browser.RefreshAsync();
        Expect.Equal(root, browser.SelectedEnvironment!.Prefix);
        Expect.Equal(3, catalog.EnvironmentCalls);
    }

    [Check("repeated refresh keeps the intended environment while an earlier refresh is pending")]
    public static async Task RepeatedRefreshKeepsSelection()
    {
        var root = Root("repeated-refresh");
        var other = Root("repeated-refresh", "envs", "science");
        var delayed = new TaskCompletionSource<IReadOnlyList<CondaEnvironment>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => ++calls == 2
                ? delayed.Task
                : Task.FromResult<IReadOnlyList<CondaEnvironment>>(
                    [Environment(root, true), Environment(other)]),
            Packages = (_, _, _) => Task.FromResult<IReadOnlyList<CondaPackage>>([])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);
        await browser.SetInstallationAsync(Installation(root));
        await browser.SelectEnvironmentAsync(browser.Environments[1]);

        var oldRefresh = browser.RefreshAsync();
        await browser.RefreshAsync();
        delayed.SetResult([Environment(root, true), Environment(other)]);
        await oldRefresh;

        Expect.Equal(other, browser.SelectedEnvironment!.Prefix);
    }

    [Check("package search matches name and version without case sensitivity")]
    public static async Task PackageSearch()
    {
        var root = Root("install");
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => Task.FromResult<IReadOnlyList<CondaEnvironment>>([Environment(root, true)]),
            Packages = (_, _, _) => Task.FromResult<IReadOnlyList<CondaPackage>>(
                [new("NumPy", "2.1.0", null, null), new("pandas", "2.2.0", null, null)])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);
        await browser.SetInstallationAsync(Installation(root));

        browser.PackageSearchText = "NUM";
        Expect.Equal(1, browser.VisiblePackages.Count);
        Expect.Equal("NumPy", browser.VisiblePackages[0].Name);
        browser.PackageSearchText = "2.2";
        Expect.Equal(1, browser.VisiblePackages.Count);
        Expect.Equal("pandas", browser.VisiblePackages[0].Name);
        browser.PackageSearchText = "missing";
        Expect.True(browser.HasFilteredNoResults, "A nonempty package list with zero matches should be distinct from an empty environment.");
        browser.PackageSearchText = "";
        Expect.Equal(2, browser.VisiblePackages.Count);
        Expect.Equal(1, catalog.PackageCalls.Count);
    }

    [Check("environment and package failures remain separate and can be retried")]
    public static async Task ErrorsAndRetry()
    {
        var root = Root("install");
        var failEnvironments = true;
        var failPackages = true;
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => failEnvironments
                ? Task.FromException<IReadOnlyList<CondaEnvironment>>(new IOException("list unavailable"))
                : Task.FromResult<IReadOnlyList<CondaEnvironment>>([Environment(root, true)]),
            Packages = (_, _, _) => failPackages
                ? Task.FromException<IReadOnlyList<CondaPackage>>(new IOException("packages unavailable"))
                : Task.FromResult<IReadOnlyList<CondaPackage>>([])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);
        await browser.SetInstallationAsync(Installation(root));
        Expect.Equal(BrowserLoadState.Error, browser.EnvironmentState);
        Expect.Equal(BrowserLoadState.Idle, browser.PackageState);
        Expect.True(browser.EnvironmentError!.Contains("list unavailable"), "List error should be visible.");

        failEnvironments = false;
        await browser.RefreshAsync();
        Expect.Equal(BrowserLoadState.Ready, browser.EnvironmentState);
        Expect.Equal(BrowserLoadState.Error, browser.PackageState);
        Expect.True(browser.PackageError!.Contains("packages unavailable"), "Package error should be visible.");

        failPackages = false;
        await browser.RefreshDetailsAsync();
        Expect.Equal(BrowserLoadState.Empty, browser.PackageState);
        Expect.Equal("未安装 Python", browser.PythonVersion);
    }

    [Check("clearing the installation cancels outstanding requests and state")]
    public static async Task ClearingInstallation()
    {
        var root = Root("install");
        var delayed = new TaskCompletionSource<IReadOnlyList<CondaEnvironment>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog
        {
            Environments = (_, _) => delayed.Task,
            Packages = (_, _, _) => Task.FromResult<IReadOnlyList<CondaPackage>>([])
        };
        using var browser = new EnvironmentBrowserViewModel(catalog);

        var oldLoad = browser.SetInstallationAsync(Installation(root));
        await browser.SetInstallationAsync(null);
        delayed.SetResult([Environment(root, true)]);
        await oldLoad;

        Expect.Equal(BrowserLoadState.Idle, browser.EnvironmentState);
        Expect.Equal(BrowserLoadState.Idle, browser.PackageState);
        Expect.Equal(0, browser.Environments.Count);
        Expect.True(browser.SelectedEnvironment is null, "No environment can remain selected without an installation.");
    }

    private static string Root(params string[] parts) =>
        Path.Combine([Path.GetTempPath(), "conda-browser-checks", .. parts]);

    private static CondaEnvironment Environment(string prefix, bool isBase = false) =>
        new(prefix, isBase ? "base" : Path.GetFileName(prefix), isBase);

    private static VerifiedInstallation Installation(string prefix) =>
        new(new CondaInfo(prefix, "25.5.1", []), new CondaLauncher(Path.Combine(prefix, "python.exe"), ["-m", "conda"]));

    private sealed class FakeCatalog : ICondaEnvironmentCatalog
    {
        public Func<VerifiedInstallation, CancellationToken, Task<IReadOnlyList<CondaEnvironment>>> Environments { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<CondaEnvironment>>([]);

        public Func<VerifiedInstallation, string, CancellationToken, Task<IReadOnlyList<CondaPackage>>> Packages { get; set; } =
            (_, _, _) => Task.FromResult<IReadOnlyList<CondaPackage>>([]);

        public int EnvironmentCalls { get; private set; }
        public List<string> PackageCalls { get; } = [];

        public Task<IReadOnlyList<CondaEnvironment>> ListEnvironmentsAsync(
            VerifiedInstallation installation, CancellationToken cancellationToken)
        {
            EnvironmentCalls++;
            return Environments(installation, cancellationToken);
        }

        public Task<IReadOnlyList<CondaPackage>> ListPackagesAsync(
            VerifiedInstallation installation, string prefix, CancellationToken cancellationToken)
        {
            PackageCalls.Add(prefix);
            return Packages(installation, prefix, cancellationToken);
        }
    }
}
