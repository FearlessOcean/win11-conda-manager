using CondaManager.Core;

namespace CondaManager.Checks;

public static class SearchViewModelChecks
{
    [Check("available package search runs only on explicit request and exposes safe results")]
    public static async Task ExplicitSearch()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installation = Installation(tree);
        var service = new FakeSearch((_, _, _, _) => Task.FromResult(new PackageSearchPage(
            [new AvailablePackage("numpy", "2.1", "py312_0", "conda-forge", "win-64")], false)));
        using var viewModel = new AvailablePackageSearchViewModel(service);

        viewModel.Query = "numpy";
        Expect.Equal(0, service.Calls);
        Expect.Equal(BrowserLoadState.Idle, viewModel.State);
        await viewModel.SearchAsync(installation, ChannelSnapshot.Inherit);

        Expect.Equal(1, service.Calls);
        Expect.Equal(BrowserLoadState.Ready, viewModel.State);
        Expect.Equal("numpy", viewModel.Items[0].Name);
        Expect.True(!viewModel.IsSearching, "Completed search must clear busy state.");
        viewModel.SelectedResult = viewModel.Items[0];
        Expect.Equal("numpy", viewModel.SelectedResult!.Name);
    }

    [Check("changing query cancels an in-flight available-package search")]
    public static async Task QueryChangeRejectsLateResult()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var delayed = new TaskCompletionSource<PackageSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSearch((_, _, _, _) => delayed.Task);
        using var viewModel = new AvailablePackageSearchViewModel(service) { Query = "numpy" };

        var oldSearch = viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);
        Expect.Equal(BrowserLoadState.Loading, viewModel.State);
        viewModel.Query = "scipy";
        delayed.SetResult(new PackageSearchPage(
            [new AvailablePackage("numpy", "2.1", "0", "defaults", "win-64")], false));
        await oldSearch;

        Expect.Equal(0, viewModel.Items.Count);
        Expect.True(viewModel.State != BrowserLoadState.Loading,
            "Changing the query must clear the stale loading state.");
    }

    [Check("new available-package search wins over an older installation or channel result")]
    public static async Task NewSearchWins()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var oldResult = new TaskCompletionSource<PackageSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSearch((_, query, channels, _) =>
            query == "numpy" && !channels.IsOverride
                ? oldResult.Task
                : Task.FromResult(new PackageSearchPage(
                    [new AvailablePackage("scipy", "1.16", "0", "conda-forge", "win-64")], true)));
        using var viewModel = new AvailablePackageSearchViewModel(service) { Query = "numpy" };
        var oldSearch = viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);

        viewModel.Query = "scipy";
        await viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Custom(["conda-forge"]));
        oldResult.SetResult(new PackageSearchPage(
            [new AvailablePackage("numpy", "2.1", "0", "defaults", "win-64")], false));
        await oldSearch;

        Expect.Equal(2, service.Calls);
        Expect.Equal("scipy", viewModel.Items[0].Name);
        Expect.True(viewModel.Truncated, "The current result's truncation state must win.");
        Expect.Equal(BrowserLoadState.Ready, viewModel.State);
    }

    [Check("available-package search cancellation and invalidation clear stale selection")]
    public static async Task CancelAndInvalidate()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var delayed = new TaskCompletionSource<PackageSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSearch((_, query, _, _) => query == "numpy"
            ? Task.FromResult(new PackageSearchPage(
                [new AvailablePackage("numpy", "2.1", "0", "defaults", "win-64")], false))
            : delayed.Task);
        using var viewModel = new AvailablePackageSearchViewModel(service) { Query = "numpy" };
        await viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);
        viewModel.SelectedResult = viewModel.Items[0];

        viewModel.Query = "scipy";
        var pending = viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);
        viewModel.Cancel();
        delayed.SetResult(new PackageSearchPage(
            [new AvailablePackage("scipy", "1.16", "0", "defaults", "win-64")], false));
        await pending;
        Expect.Equal(0, viewModel.Items.Count);
        Expect.True(viewModel.State != BrowserLoadState.Loading,
            "Cancel must leave an inspectable, non-busy state.");

        viewModel.Invalidate();
        Expect.Equal(0, viewModel.Items.Count);
        Expect.True(viewModel.SelectedResult is null, "Changing installation or channels clears selection.");
    }

    [Check("external cancellation clears available-package loading even if the service returns late")]
    public static async Task ExternalCancellationClearsLoading()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var delayed = new TaskCompletionSource<PackageSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSearch((_, _, _, _) => delayed.Task);
        using var viewModel = new AvailablePackageSearchViewModel(service) { Query = "numpy" };
        using var cancellation = new CancellationTokenSource();

        var pending = viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit, cancellation.Token);
        cancellation.Cancel();
        delayed.SetResult(new PackageSearchPage(
            [new AvailablePackage("numpy", "2.1", "0", "defaults", "win-64")], false));
        await pending;

        Expect.Equal(0, viewModel.Items.Count);
        Expect.Equal(BrowserLoadState.Idle, viewModel.State);
    }

    [Check("available-package search distinguishes empty results and failures")]
    public static async Task EmptyAndError()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var service = new FakeSearch((_, query, _, _) => query == "missing"
            ? Task.FromResult(new PackageSearchPage([], false))
            : Task.FromException<PackageSearchPage>(new CondaOperationException("搜索失败")));
        using var viewModel = new AvailablePackageSearchViewModel(service) { Query = "missing" };

        await viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);
        Expect.Equal(BrowserLoadState.Empty, viewModel.State);
        viewModel.Query = "broken";
        await viewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);
        Expect.Equal(BrowserLoadState.Error, viewModel.State);
        Expect.True(viewModel.Error?.Contains("搜索失败", StringComparison.Ordinal) == true,
            "Search errors should be available to the UI.");

        var unexpected = new FakeSearch((_, _, _, _) =>
            Task.FromException<PackageSearchPage>(new IOException(
                "https://user:PRIVATE_TOKEN@example.invalid/channel")));
        using var safeViewModel = new AvailablePackageSearchViewModel(unexpected) { Query = "numpy" };
        await safeViewModel.SearchAsync(Installation(tree), ChannelSnapshot.Inherit);
        Expect.True(safeViewModel.Error is not null &&
                    !safeViewModel.Error.Contains("PRIVATE_TOKEN", StringComparison.Ordinal),
            "Unexpected process errors must not expose channel credentials.");
    }

    private static VerifiedInstallation Installation(DiscoveryChecks.DiscoveryTree tree)
    {
        var root = tree.CreateRoot("search-installation");
        return new VerifiedInstallation(new CondaInfo(root, "25.5.1", []),
            new CondaLauncher(Path.Combine(root, "python.exe"), ["-m", "conda"]));
    }

    private sealed class FakeSearch(
        Func<VerifiedInstallation, string, ChannelSnapshot, CancellationToken, Task<PackageSearchPage>> response)
        : ICondaPackageSearch
    {
        public int Calls { get; private set; }

        public Task<PackageSearchPage> SearchAsync(VerifiedInstallation installation, string query,
            ChannelSnapshot channels, CancellationToken cancellationToken)
        {
            Calls++;
            return response(installation, query, channels, cancellationToken);
        }

        public Task<IReadOnlyList<string>> GetConfiguredChannelsAsync(VerifiedInstallation installation,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
