using System.Text.Json;
using System.Diagnostics;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class SelectionChecks
{
    [Check("settings round trip a normalized selected root")]
    public static void SettingsRoundTrip()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var root = tree.CreateRoot("selected");
        var settings = new SelectionSettings(Path.Combine(tree.Base, "settings", "settings.json"));

        Expect.True(settings.Load() is null, "Missing settings must load as no selection.");
        settings.Save(root + Path.DirectorySeparatorChar);
        Expect.Equal(root, settings.Load());
    }

    [Check("settings ignore malformed JSON and invalid selected roots")]
    public static void SettingsIgnoreInvalidContent()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        tree.CreateDirectory();
        var path = Path.Combine(tree.Base, "settings.json");
        var settings = new SelectionSettings(path);

        File.WriteAllText(path, "{");
        Expect.True(settings.Load() is null, "Malformed JSON must not crash startup.");
        File.WriteAllText(path, JsonSerializer.Serialize(new { selectedRoot = "relative-root" }));
        Expect.True(settings.Load() is null, "Relative persisted roots must not be used.");
    }

    [Check("view model prefers a verified saved installation and persists a new selection")]
    public static async Task SavedSelectionAndSwitching()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var first = tree.CreateRoot("first");
        var second = tree.CreateRoot("second");
        tree.Touch(Path.Combine(first, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(second, "Scripts", "conda.exe"));
        var runner = new DiscoveryChecks.DiscoveryRunner();
        runner.Return(first, DiscoveryChecks.ValidInfo(first));
        runner.Return(second, DiscoveryChecks.ValidInfo(second));
        var settings = new SelectionSettings(Path.Combine(tree.Base, "settings.json"));
        settings.Save(second);
        var viewModel = NewViewModel(runner, settings, _ => [first, second]);

        await viewModel.ScanAsync();

        Expect.Equal(2, viewModel.Installations.Count);
        Expect.Equal(second, viewModel.SelectedInstallation!.Info.RootPrefix);
        Expect.True(!viewModel.HasNoResults, "Verified installations must clear no-results state.");
        viewModel.SelectedInstallation = viewModel.Installations[0];
        Expect.Equal(first, settings.Load());
    }

    [Check("view model falls back when the saved root does not verify")]
    public static async Task InvalidSavedRootFallsBack()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var invalid = tree.CreateRoot("invalid-saved");
        var valid = tree.CreateRoot("valid");
        tree.Touch(Path.Combine(valid, "Scripts", "conda.exe"));
        var runner = new DiscoveryChecks.DiscoveryRunner();
        runner.Return(valid, DiscoveryChecks.ValidInfo(valid));
        var settings = new SelectionSettings(Path.Combine(tree.Base, "settings.json"));
        settings.Save(invalid);
        var viewModel = NewViewModel(runner, settings, saved => [saved!, valid]);

        await viewModel.ScanAsync();

        Expect.Equal(valid, viewModel.SelectedInstallation!.Info.RootPrefix);
        Expect.Equal(valid, settings.Load());
        Expect.True(viewModel.Diagnostics.Any(diagnostic => diagnostic.Contains(invalid, StringComparison.OrdinalIgnoreCase)),
            "The invalid saved root should appear in diagnostics.");
    }

    [Check("view model shows no-results state and failure details")]
    public static async Task NoResultsState()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var invalid = tree.CreateRoot("invalid");
        var viewModel = NewViewModel(new DiscoveryChecks.DiscoveryRunner(),
            new SelectionSettings(Path.Combine(tree.Base, "settings.json")), _ => [invalid]);

        await viewModel.ScanAsync();

        Expect.Equal(0, viewModel.Installations.Count);
        Expect.True(viewModel.SelectedInstallation is null, "No invalid root may be selected.");
        Expect.True(viewModel.HasNoResults, "A finished empty scan must show the no-results state.");
        Expect.True(!viewModel.IsScanning, "Scanning flag must reset.");
        Expect.True(viewModel.Diagnostics.Count > 0, "The failed candidate must be explained.");
    }

    [Check("manual root validation adds and selects only a verified installation")]
    public static async Task ManualRootValidation()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var valid = tree.CreateRoot("manual-valid");
        var invalid = tree.CreateRoot("manual-invalid");
        tree.Touch(Path.Combine(valid, "Scripts", "conda.exe"));
        var runner = new DiscoveryChecks.DiscoveryRunner();
        runner.Return(valid, DiscoveryChecks.ValidInfo(valid));
        var settings = new SelectionSettings(Path.Combine(tree.Base, "settings.json"));
        var viewModel = NewViewModel(runner, settings, _ => []);

        await viewModel.ScanAsync();
        Expect.True(viewModel.HasNoResults, "Empty discovery should show no results.");
        await viewModel.AddManualRootAsync(invalid);
        Expect.Equal(0, viewModel.Installations.Count);
        Expect.True(viewModel.Diagnostics.Any(diagnostic => diagnostic.Contains(invalid, StringComparison.OrdinalIgnoreCase)),
            "A failed manual root should be diagnosed.");
        await viewModel.AddManualRootAsync(valid);
        Expect.Equal(1, viewModel.Installations.Count);
        Expect.Equal(valid, viewModel.SelectedInstallation!.Info.RootPrefix);
        Expect.Equal(valid, settings.Load());
        Expect.True(!viewModel.HasNoResults, "Adding a working installation should hide no-results state.");
        await viewModel.AddManualRootAsync(valid + Path.DirectorySeparatorChar);
        Expect.Equal(1, viewModel.Installations.Count);
    }

    [Check("settings write failures leave selection available and report a diagnostic")]
    public static async Task SettingsWriteFailureIsDiagnostic()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var root = tree.CreateRoot("working");
        tree.Touch(Path.Combine(root, "Scripts", "conda.exe"));
        var runner = new DiscoveryChecks.DiscoveryRunner();
        runner.Return(root, DiscoveryChecks.ValidInfo(root));
        var settingsPath = tree.CreateDirectory("settings-is-a-directory");
        var viewModel = NewViewModel(runner, new SelectionSettings(settingsPath), _ => [root]);

        await viewModel.ScanAsync();

        Expect.Equal(root, viewModel.SelectedInstallation!.Info.RootPrefix);
        Expect.True(viewModel.Diagnostics.Any(diagnostic => diagnostic.Contains("保存", StringComparison.Ordinal)),
            "Settings write failure should be shown without losing the selection.");
    }

    [Check("view model cancellation resets scanning state and visits no later roots")]
    public static async Task ScanCancellation()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var first = tree.CreateRoot("cancel-first");
        var second = tree.CreateRoot("cancel-second");
        tree.Touch(Path.Combine(first, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(second, "Scripts", "conda.exe"));
        using var cancellation = new CancellationTokenSource();
        var runner = new DiscoveryChecks.DiscoveryRunner();
        runner.Cancel(first, cancellation);
        var viewModel = NewViewModel(runner,
            new SelectionSettings(Path.Combine(tree.Base, "settings.json")), _ => [first, second]);

        await Expect.ThrowsAsync<OperationCanceledException>(() => viewModel.ScanAsync(cancellation.Token));

        Expect.True(!viewModel.IsScanning, "Cancellation must reset scanning state.");
        Expect.Equal(1, runner.Calls.Count);
    }

    [Check("slow candidate lookup does not block the caller")]
    public static async Task SlowCandidateLookupDoesNotBlockCaller()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        using var gate = new ManualResetEventSlim(false);
        var viewModel = NewViewModel(new DiscoveryChecks.DiscoveryRunner(),
            new SelectionSettings(Path.Combine(tree.Base, "settings.json")), _ =>
            {
                gate.Wait(TimeSpan.FromSeconds(1));
                return [];
            });

        var clock = Stopwatch.StartNew();
        var scan = viewModel.ScanAsync();
        var callDuration = clock.Elapsed;
        gate.Set();
        await scan;

        Expect.True(callDuration < TimeSpan.FromMilliseconds(500),
            $"Starting a scan blocked the caller for {callDuration}.");
    }

    private static MainViewModel NewViewModel(DiscoveryChecks.DiscoveryRunner runner,
        SelectionSettings settings, Func<string?, IReadOnlyList<string>> rootsProvider) =>
        new(new CondaDiscoveryService(new CondaLauncherValidator(runner)), settings, rootsProvider);
}
