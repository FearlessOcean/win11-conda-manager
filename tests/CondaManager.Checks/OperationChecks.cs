using System.Text.Json;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class OperationChecks
{
    [Check("create uses the previewed prefix, Python spec, verified launcher, and disables automatic error reporting")]
    public static async Task CreateUsesExactArguments()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installation = Installation(tree);
        var parent = tree.CreateDirectory("envs with spaces");
        var plan = EnvironmentCreationPlanner.Build("研究_01", parent, "3.12");
        var runner = new FakeRunner((_, _, _) =>
        {
            MakeEnvironment(plan.Prefix);
            return Task.FromResult(Success(plan.Prefix));
        });

        await new CondaEnvironmentOperator(runner).CreateAsync(installation, plan, CancellationToken.None);

        Expect.Equal(1, runner.Calls.Count);
        var call = runner.Calls[0];
        Expect.Equal(installation.Launcher.ExecutablePath, call.Launcher.ExecutablePath);
        Expect.True(call.Launcher.LeadingArguments.SequenceEqual(installation.Launcher.LeadingArguments),
            "The verified launcher's leading arguments must be retained.");
        Expect.True(call.Arguments.SequenceEqual(new[]
        {
            "create", "--prefix", plan.Prefix, "python=3.12", "--yes", "--json"
        }), "Create arguments must preserve the exact previewed prefix as one argument.");
        Expect.Equal("false", call.Launcher.EnvironmentOverrides["CONDA_REPORT_ERRORS"]);
        Expect.True(call.Timeout >= TimeSpan.FromMinutes(10), "Creation needs a bounded but practical timeout.");
    }

    [Check("create without a version still installs Python")]
    public static async Task CreateDefaultPython()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var plan = EnvironmentCreationPlanner.Build("plain", tree.CreateDirectory("envs"), null);
        var runner = new FakeRunner((_, _, _) =>
        {
            MakeEnvironment(plan.Prefix);
            return Task.FromResult(Success(plan.Prefix));
        });

        await new CondaEnvironmentOperator(runner).CreateAsync(Installation(tree), plan, CancellationToken.None);

        Expect.Equal("python", runner.Calls[0].Arguments[3]);
    }

    [Check("create refuses an existing destination before Conda can replace it")]
    public static async Task CreateRejectsExistingTarget()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var parent = tree.CreateDirectory("envs");
        var plan = EnvironmentCreationPlanner.Build("already", parent, "3.11");
        tree.Touch(Path.Combine(plan.Prefix, "keep.txt"));
        var runner = new FakeRunner((_, _, _) => Task.FromResult(Success(plan.Prefix)));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).CreateAsync(Installation(tree), plan, CancellationToken.None));

        Expect.Equal(0, runner.Calls.Count);
        Expect.True(File.Exists(Path.Combine(plan.Prefix, "keep.txt")),
            "The pre-existing destination must remain untouched.");
    }

    [Check("create rejects an existing file at the target")]
    public static async Task CreateRejectsExistingFile()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var parent = tree.CreateDirectory("envs");
        var plan = EnvironmentCreationPlanner.Build("filetarget", parent, null);
        tree.Touch(plan.Prefix);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(Success(plan.Prefix)));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).CreateAsync(Installation(tree), plan, CancellationToken.None));
        Expect.Equal(0, runner.Calls.Count);
    }

    [Check("create treats Conda failure JSON as an error without exposing traceback")]
    public static async Task CreateFailureSanitizesOutput()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var plan = EnvironmentCreationPlanner.Build("broken", tree.CreateDirectory("envs"), "3.12");
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(1,
            "{\"error\":\"PackagesNotFoundError: python\",\"traceback\":\"PRIVATE_TRACEBACK\",\"command\":\"PRIVATE_COMMAND\"}", "")));

        var failure = await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).CreateAsync(Installation(tree), plan, CancellationToken.None));

        Expect.True(failure.Message.Contains("PackagesNotFoundError", StringComparison.Ordinal),
            "The concise Conda error should be actionable.");
        Expect.True(!failure.Message.Contains("PRIVATE_TRACEBACK", StringComparison.Ordinal) &&
                    !failure.Message.Contains("PRIVATE_COMMAND", StringComparison.Ordinal),
            "Raw error JSON and traceback must stay private.");
    }

    [Check("create requires an actual Conda history marker after a reported success")]
    public static async Task CreateSuccessRequiresMarker()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var plan = EnvironmentCreationPlanner.Build("missing_marker", tree.CreateDirectory("envs"), null);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(Success(plan.Prefix)));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).CreateAsync(Installation(tree), plan, CancellationToken.None));
    }

    [Check("create propagates user cancellation")]
    public static async Task CreateCancellation()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var plan = EnvironmentCreationPlanner.Build("cancel_me", tree.CreateDirectory("envs"), null);
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner((_, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ProcessResult>(token);
        });

        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            new CondaEnvironmentOperator(runner).CreateAsync(Installation(tree), plan, cancellation.Token));
    }

    [Check("create keeps a completed success when cancellation arrives after the child exits")]
    public static async Task CreateCompletionWinsLateCancellation()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var plan = EnvironmentCreationPlanner.Build("finished", tree.CreateDirectory("envs"), null);
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner((_, _, _) =>
        {
            MakeEnvironment(plan.Prefix);
            cancellation.Cancel();
            return Task.FromResult(Success(plan.Prefix));
        });

        await new CondaEnvironmentOperator(runner).CreateAsync(
            Installation(tree), plan, cancellation.Token);

        Expect.True(File.Exists(Path.Combine(plan.Prefix, "conda-meta", "history")),
            "A completed environment must be reported as created for prefix persistence.");
    }

    [Check("delete protects the selected base, known roots, their ancestors, and the active prefix")]
    public static async Task DeleteProtectsCriticalPaths()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installation = Installation(tree);
        var knownRoot = tree.CreateRoot("other-conda");
        var active = tree.CreateRoot("active-env");
        MakeEnvironment(installation.Info.RootPrefix);
        MakeEnvironment(knownRoot);
        MakeEnvironment(active);
        MakeEnvironment(tree.Base);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var operatorService = new CondaEnvironmentOperator(runner);
        var protectedRoots = new[] { knownRoot };

        foreach (var prefix in new[]
                 {
                     installation.Info.RootPrefix,
                     knownRoot,
                     tree.Base,
                     active,
                     Path.GetPathRoot(tree.Base)!
                 })
        {
            await Expect.ThrowsAsync<CondaOperationException>(() => operatorService.DeleteAsync(
                installation, prefix, protectedRoots, active, CancellationToken.None));
        }
        Expect.Equal(0, runner.Calls.Count);
    }

    [Check("delete refuses an ordinary directory without Conda history")]
    public static async Task DeleteRejectsNonCondaDirectory()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var plain = tree.CreateDirectory("plain");
        tree.Touch(Path.Combine(plain, "unrelated.txt"));
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).DeleteAsync(
                Installation(tree), plain, [], null, CancellationToken.None));
        Expect.Equal(0, runner.Calls.Count);
        Expect.True(File.Exists(Path.Combine(plain, "unrelated.txt")),
            "An unrelated directory must remain untouched.");
    }

    [Check("delete refuses a parent of the currently active environment")]
    public static async Task DeleteRejectsParentOfActiveEnvironment()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var target = tree.CreateDirectory("parent-env");
        var active = Path.Combine(target, "nested-active");
        MakeEnvironment(target);
        MakeEnvironment(active);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).DeleteAsync(
                Installation(tree), target, [], active, CancellationToken.None));

        Expect.Equal(0, runner.Calls.Count);
        Expect.True(File.Exists(Path.Combine(active, "conda-meta", "history")),
            "The active nested environment must remain untouched.");
    }

    [Check("delete stops when an active prefix has no verifiable directory identity")]
    public static async Task DeleteRejectsUnverifiableActivePrefix()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var target = tree.CreateDirectory("delete-target");
        MakeEnvironment(target);
        var missingActive = Path.Combine(tree.Base, "missing-active");
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var failure = await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).DeleteAsync(
                Installation(tree), target, [], missingActive, CancellationToken.None));

        Expect.True(failure.Message.Contains("无法确认", StringComparison.Ordinal),
            "An unknown active directory identity must block deletion with a clear reason.");
        Expect.Equal(0, runner.Calls.Count);
    }

    [Check("delete rejects Windows device-path aliases and short-name style paths")]
    public static async Task DeleteRejectsPathAliases()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var target = tree.CreateDirectory("alias-target");
        var shortNameStyle = tree.CreateDirectory("env~1");
        MakeEnvironment(target);
        MakeEnvironment(shortNameStyle);
        var extendedAlias = @"\\?\" + target;
        var deviceAlias = @"\\.\" + target;
        var forwardExtendedAlias = "//?/" + target.Replace('\\', '/');
        var forwardDeviceAlias = "//./" + target.Replace('\\', '/');
        var mixedAlias = @"\\?/" + target.Replace('\\', '/');
        Expect.True(Directory.Exists(extendedAlias) && Directory.Exists(deviceAlias) &&
                    Directory.Exists(forwardExtendedAlias) && Directory.Exists(forwardDeviceAlias) &&
                    Directory.Exists(mixedAlias),
            "Windows device paths must resolve to the same disposable target in this check.");
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var service = new CondaEnvironmentOperator(runner);
        var installation = Installation(tree);

        foreach (var prefix in new[]
                 { extendedAlias, deviceAlias, forwardExtendedAlias, forwardDeviceAlias, mixedAlias, shortNameStyle })
            await Expect.ThrowsAsync<CondaOperationException>(() =>
                service.DeleteAsync(installation, prefix, [], null, CancellationToken.None));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.DeleteAsync(installation, target, [extendedAlias], null, CancellationToken.None));
        Expect.Equal(0, runner.Calls.Count);
        Expect.True(File.Exists(Path.Combine(target, "conda-meta", "history")),
            "The aliased Conda environment must remain untouched.");
    }

    [Check("delete uses the verified launcher and requires the Conda directory to disappear")]
    public static async Task DeleteUsesExactArguments()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installation = Installation(tree);
        var prefix = tree.CreateDirectory("envs", "delete me");
        MakeEnvironment(prefix);
        tree.Touch(Path.Combine(prefix, "extra.txt"));
        var runner = new FakeRunner((_, _, _) =>
        {
            DeleteInsideTree(prefix, tree.Base);
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        await new CondaEnvironmentOperator(runner).DeleteAsync(
            installation, prefix, [installation.Info.RootPrefix], null, CancellationToken.None);

        Expect.Equal(1, runner.Calls.Count);
        var call = runner.Calls[0];
        Expect.True(call.Arguments.SequenceEqual(new[]
        {
            "env", "remove", "--prefix", prefix, "--yes", "--json"
        }), "Delete must use one absolute prefix argument and JSON mode.");
        Expect.Equal("false", call.Launcher.EnvironmentOverrides["CONDA_REPORT_ERRORS"]);
        Expect.True(call.Timeout >= TimeSpan.FromMinutes(5), "Delete needs a bounded timeout.");
    }

    [Check("delete reports an uncertain result if Conda exits zero but target remains")]
    public static async Task DeleteSuccessMustRemoveTarget()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var prefix = tree.CreateDirectory("envs", "still_here");
        MakeEnvironment(prefix);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).DeleteAsync(
                Installation(tree), prefix, [], null, CancellationToken.None));
        Expect.True(Directory.Exists(prefix), "An uncertain deletion result must not hide the remaining directory.");
    }

    [Check("delete keeps a completed success when cancellation arrives after the child exits")]
    public static async Task DeleteCompletionWinsLateCancellation()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var prefix = tree.CreateDirectory("envs", "finished-delete");
        MakeEnvironment(prefix);
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner((_, _, _) =>
        {
            DeleteInsideTree(prefix, tree.Base);
            cancellation.Cancel();
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        await new CondaEnvironmentOperator(runner).DeleteAsync(
            Installation(tree), prefix, [], null, cancellation.Token);

        Expect.True(!Directory.Exists(prefix),
            "A completed deletion must be reported as deleted for prefix-store cleanup.");
    }

    [Check("delete reports concise Conda errors without exposing raw JSON")]
    public static async Task DeleteFailureSanitizesOutput()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var prefix = tree.CreateDirectory("envs", "blocked");
        MakeEnvironment(prefix);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(9,
            "{\"error\":\"Permission denied\",\"traceback\":\"PRIVATE_TRACEBACK\"}", "")));

        var failure = await Expect.ThrowsAsync<CondaOperationException>(() =>
            new CondaEnvironmentOperator(runner).DeleteAsync(
                Installation(tree), prefix, [], null, CancellationToken.None));

        Expect.True(failure.Message.Contains("Permission denied", StringComparison.Ordinal),
            "The short Conda error should be displayed.");
        Expect.True(!failure.Message.Contains("PRIVATE_TRACEBACK", StringComparison.Ordinal),
            "Traceback must not be exposed in the UI error.");
    }

    private static VerifiedInstallation Installation(DiscoveryChecks.DiscoveryTree tree)
    {
        var root = tree.CreateRoot("base-conda");
        return new VerifiedInstallation(new CondaInfo(root, "25.5.1", []),
            new CondaLauncher(Path.Combine(root, "python.exe"), ["-m", "conda"]));
    }

    private static ProcessResult Success(string prefix) =>
        new(0, JsonSerializer.Serialize(new { success = true, prefix }), "");

    private static void MakeEnvironment(string prefix)
    {
        var marker = Path.Combine(prefix, "conda-meta", "history");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "# test environment");
    }

    private static void DeleteInsideTree(string target, string expectedParent)
    {
        var root = Path.GetFullPath(expectedParent);
        var resolved = Path.GetFullPath(target);
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Test deletion escaped its disposable tree.");
        Directory.Delete(resolved, recursive: true);
    }

    private sealed record Call(CondaLauncher Launcher, IReadOnlyList<string> Arguments, TimeSpan Timeout);

    private sealed class FakeRunner(
        Func<CondaLauncher, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> response) : IProcessRunner
    {
        public List<Call> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(launcher, arguments.ToArray(), timeout));
            return response(launcher, arguments, cancellationToken);
        }
    }
}
