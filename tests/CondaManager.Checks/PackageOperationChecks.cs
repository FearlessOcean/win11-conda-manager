using System.Text.Json;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class PackageOperationChecks
{
    [Check("package install spec accepts one safe name and optional version")]
    public static void InstallSpecValidation()
    {
        Expect.Equal("numpy", CondaPackageOperator.BuildInstallSpec("numpy", null));
        Expect.Equal("numpy=1.26.4", CondaPackageOperator.BuildInstallSpec("numpy", "1.26.4"));
        foreach (var name in new[] { "", "-bad", "conda-forge::numpy", "numpy scipy", "../numpy", "numpy\n" })
            Expect.Throws<ArgumentException>(() => CondaPackageOperator.BuildInstallSpec(name, null));
        foreach (var version in new[] { ">=1.2", "1.2 3", "1.2::x", "../1.2", " ", " 1.26.4 ", "1.2\n" })
            Expect.Throws<ArgumentException>(() => CondaPackageOperator.BuildInstallSpec("numpy", version));
    }

    [Check("package dry run uses exact install arguments and parses dependency transaction")]
    public static async Task PreviewInstallTransaction()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", "1.26");
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0,
            JsonSerializer.Serialize(new
            {
                success = true,
                prefix = request.Prefix,
                dry_run = true,
                actions = new
                {
                    PREFIX = request.Prefix,
                    FETCH = new[] { new { name = "numpy", version = "1.26.4", build_string = "py312_0", url = "PRIVATE_TOKEN" } },
                    UNLINK = new[] { new { name = "old-dependency", version = "1.0", build_string = "0" } },
                    LINK = new[] { new { name = "numpy", version = "1.26.4", build_string = "py312_0" } }
                }
            }), "")));

        var preview = await new CondaPackageOperator(runner).PreviewAsync(request, CancellationToken.None);

        Expect.True(preview.HasChanges, "A transaction with links and unlinks needs confirmation.");
        Expect.Equal("numpy", preview.ToInstall[0].Name);
        Expect.Equal("old-dependency", preview.ToRemove[0].Name);
        Expect.Equal("numpy", preview.ToFetch[0].Name);
        Expect.True(!preview.ToInstall[0].ToString()!.Contains("PRIVATE_TOKEN", StringComparison.Ordinal),
            "Preview must not expose channel URLs or tokens.");
        Expect.Equal(1, runner.Calls.Count);
        Expect.True(runner.Calls[0].Arguments.SequenceEqual(new[]
        {
            "install", "--prefix", request.Prefix, "numpy=1.26", "--dry-run", "--json"
        }), "Conda install dry-run must use one exact package spec argument.");
        Expect.Equal(request.Installation.Launcher.ExecutablePath, runner.Calls[0].Launcher.ExecutablePath);
        Expect.Equal("false", runner.Calls[0].Launcher.EnvironmentOverrides["CONDA_REPORT_ERRORS"]);
    }

    [Check("package update and remove target one selected Conda package")]
    public static async Task PreviewSelectedCondaPackage()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installed = new CondaPackage("scipy", "1.12", "py312_0", "conda-forge");
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0,
            "{\"success\":true,\"message\":\"All requested packages already installed.\"}", "")));
        var service = new CondaPackageOperator(runner);

        foreach (var action in new[] { PackageAction.Update, PackageAction.Remove })
        {
            var request = Request(tree, action, "scipy", null, installed);
            var preview = await service.PreviewAsync(request, CancellationToken.None);
            Expect.True(!preview.HasChanges, "A no-op needs no execution.");
            var verb = action == PackageAction.Update ? "update" : "remove";
            Expect.True(runner.Calls[^1].Arguments.SequenceEqual(new[]
            {
                verb, "--prefix", request.Prefix, "scipy", "--dry-run", "--json"
            }), "Update/remove must use the selected package name as one argument.");
        }
    }

    [Check("package update and remove accept installed Conda names with leading underscores")]
    public static async Task PreviewUnderscoreNamedInstalledPackage()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installed = new CondaPackage("_anaconda_depends", "2025.1", "0", "defaults");
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0,
            "{\"success\":true,\"message\":\"All requested packages already installed.\"}", "")));
        var service = new CondaPackageOperator(runner);

        foreach (var action in new[] { PackageAction.Update, PackageAction.Remove })
        {
            var request = Request(tree, action, installed.Name, null, installed);
            await service.PreviewAsync(request, CancellationToken.None);
            Expect.Equal(installed.Name, runner.Calls[^1].Arguments[3]);
        }
        Expect.Equal(2, runner.Calls.Count);
    }

    [Check("package mutation rejects base, unknown package origin, and unsafe path aliases")]
    public static async Task RejectUnsafeTargetsAndOrigins()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var installation = Installation(tree);
        MakeEnvironment(installation.Info.RootPrefix);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "{}", "")));
        var service = new CondaPackageOperator(runner);
        var rootRequest = new PackageOperationRequest(installation, installation.Info.RootPrefix,
            PackageAction.Install, "numpy", null, null, []);
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.PreviewAsync(rootRequest, CancellationToken.None));

        var ordinary = Request(tree, PackageAction.Remove, "wheel", null,
            new CondaPackage("wheel", "1.0", "pypi_0", "pypi"));
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.PreviewAsync(ordinary, CancellationToken.None));
        var unknown = ordinary with { InstalledPackage = new CondaPackage("wheel", "1.0", null, null) };
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.PreviewAsync(unknown, CancellationToken.None));
        var alias = ordinary with
        {
            Action = PackageAction.Install,
            InstalledPackage = null,
            Prefix = @"\\?\" + ordinary.Prefix
        };
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.PreviewAsync(alias, CancellationToken.None));
        Expect.Equal(0, runner.Calls.Count);
    }

    [Check("package preview rejects malformed, failed, or mismatched JSON before execution")]
    public static async Task RejectUnverifiablePreview()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        foreach (var result in new[]
                 {
                     new ProcessResult(0, "{}", ""),
                     new ProcessResult(0, "{\"success\":true,\"actions\":[]}", ""),
                     new ProcessResult(0, "{\"success\":true,\"actions\":{\"LINK\":{}}}", ""),
                     new ProcessResult(0, "{\"success\":true,\"message\":\"An unrecognized warning.\"}", ""),
                     new ProcessResult(0, "{\"success\":true,\"actions\":{\"UNKNOWN\":[]}}", ""),
                     new ProcessResult(0, "{\"success\":true,\"actions\":{\"LINK\":[],\"UNKNOWN\":[]}}", ""),
                     new ProcessResult(0, "{\"success\":true,\"actions\":{\"PREFIX\":\"C:\\\\wrong\",\"LINK\":[]}}", ""),
                     new ProcessResult(0, "{\"success\":true,\"prefix\":\"C:\\\\wrong\",\"actions\":{}}", ""),
                     new ProcessResult(0, "{\"success\":true,\"dry_run\":false,\"actions\":{}}", ""),
                     new ProcessResult(1, "{\"error\":\"Package missing\\nPRIVATE_TRACEBACK\"}", "")
                 })
        {
            var runner = new FakeRunner((_, _, _) => Task.FromResult(result));
            var failure = await Expect.ThrowsAsync<CondaOperationException>(() =>
                new CondaPackageOperator(runner).PreviewAsync(request, CancellationToken.None));
            Expect.True(!failure.Message.Contains("PRIVATE_TRACEBACK", StringComparison.Ordinal),
                "Raw traceback must not enter the error message.");
            Expect.Equal(1, runner.Calls.Count);
        }
    }

    [Check("package preview never displays a build field or error containing credentialed URLs")]
    public static async Task PreviewDoesNotExposeCredentials()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        const string secret = "https://user:PRIVATE_TOKEN@example.invalid/channel";
        var dangerousBuild = JsonSerializer.Serialize(new
        {
            success = true,
            actions = new { LINK = new[] { new { name = "numpy", version = "1.26.4", build_string = secret } } }
        });
        foreach (var result in new[]
                 {
                     new ProcessResult(0, dangerousBuild, ""),
                     new ProcessResult(1, JsonSerializer.Serialize(new { error = $"CondaHTTPError: {secret}" }), "")
                 })
        {
            var runner = new FakeRunner((_, _, _) => Task.FromResult(result));
            var failure = await Expect.ThrowsAsync<CondaOperationException>(() =>
                new CondaPackageOperator(runner).PreviewAsync(request, CancellationToken.None));
            Expect.True(!failure.Message.Contains("PRIVATE_TOKEN", StringComparison.Ordinal) &&
                        !failure.Message.Contains(secret, StringComparison.Ordinal),
                "A Conda result must never display a credentialed URL.");
        }
    }

    [Check("package preview accepts Conda dependency names that begin with underscores")]
    public static async Task PreviewAllowsCondaDependencyNames()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0,
            "{\"success\":true,\"actions\":{\"LINK\":[{\"name\":\"_openmp_mutex\",\"version\":\"4.5\"}]}}", "")));

        var preview = await new CondaPackageOperator(runner).PreviewAsync(request, CancellationToken.None);

        Expect.Equal("_openmp_mutex", preview.ToInstall[0].Name);
    }

    [Check("package no-op preview never reaches the apply command")]
    public static async Task NoOpCannotApply()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        var runner = new FakeRunner((_, _, _) => Task.FromResult(new ProcessResult(0,
            "{\"success\":true,\"message\":\"All requested packages already installed.\"}", "")));
        var service = new CondaPackageOperator(runner);
        var preview = await service.PreviewAsync(request, CancellationToken.None);

        Expect.True(!preview.HasChanges, "A message-only successful response is a no-op.");
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.ApplyAsync(request, preview, CancellationToken.None));
        Expect.Equal(1, runner.Calls.Count);
    }

    [Check("package apply requires the matching preview and uses yes without dry run")]
    public static async Task ApplyConfirmedTransaction()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", "1.26");
        var runner = new FakeRunner((_, arguments, _) => Task.FromResult(new ProcessResult(0,
            arguments.Contains("--dry-run")
                ? JsonSerializer.Serialize(new
                {
                    success = true,
                    prefix = request.Prefix,
                    dry_run = true,
                    actions = new { LINK = new[] { new { name = "numpy", version = "1.26.4" } } }
                })
                : JsonSerializer.Serialize(new { success = true, prefix = request.Prefix }), "")));
        var service = new CondaPackageOperator(runner);
        var preview = await service.PreviewAsync(request, CancellationToken.None);
        var different = request with { PackageName = "scipy" };
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.ApplyAsync(different, preview, CancellationToken.None));

        await service.ApplyAsync(request, preview, CancellationToken.None);

        Expect.Equal(2, runner.Calls.Count);
        Expect.True(runner.Calls[1].Arguments.SequenceEqual(new[]
        {
            "install", "--prefix", request.Prefix, "numpy=1.26", "--yes", "--json"
        }), "Apply must use yes and omit dry-run.");
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.ApplyAsync(request, preview, CancellationToken.None));
        Expect.Equal(2, runner.Calls.Count);
    }

    [Check("package apply rejects fabricated or modified preview objects")]
    public static async Task ApplyRequiresIssuedImmutablePreview()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        var runner = new FakeRunner((_, arguments, _) => Task.FromResult(new ProcessResult(0,
            arguments.Contains("--dry-run")
                ? "{\"success\":true,\"actions\":{\"LINK\":[{\"name\":\"numpy\",\"version\":\"1.0\"}]}}"
                : "{\"success\":true}", "")));
        var service = new CondaPackageOperator(runner);
        var fabricated = new PackagePreview(request, [new PackageChange("numpy", "1.0", null)], [], []);
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.ApplyAsync(request, fabricated, CancellationToken.None));
        var issued = await service.PreviewAsync(request, CancellationToken.None);
        var modified = issued with { ToInstall = [new PackageChange("other", "9.0", null)] };
        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.ApplyAsync(request, modified, CancellationToken.None));
        Expect.Throws<NotSupportedException>(() =>
            ((IList<PackageChange>)issued.ToInstall).Add(new PackageChange("other", "9.0", null)));
        Expect.Equal(1, runner.Calls.Count);
    }

    [Check("package apply rejects a Conda transaction targeting another prefix")]
    public static async Task ApplyRejectsNestedPrefixMismatch()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        var runner = new FakeRunner((_, arguments, _) => Task.FromResult(new ProcessResult(0,
            arguments.Contains("--dry-run")
                ? "{\"success\":true,\"actions\":{\"LINK\":[{\"name\":\"numpy\",\"version\":\"1.0\"}]}}"
                : JsonSerializer.Serialize(new
                {
                    success = true,
                    prefix = request.Prefix,
                    actions = new { PREFIX = @"C:\wrong", LINK = Array.Empty<object>() }
                }), "")));
        var service = new CondaPackageOperator(runner);
        var preview = await service.PreviewAsync(request, CancellationToken.None);

        await Expect.ThrowsAsync<CondaOperationException>(() =>
            service.ApplyAsync(request, preview, CancellationToken.None));
        Expect.Equal(2, runner.Calls.Count);
    }

    [Check("package preview and apply propagate cancellation")]
    public static async Task Cancellation()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var request = Request(tree, PackageAction.Install, "numpy", null);
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner((_, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ProcessResult>(token);
        });
        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            new CondaPackageOperator(runner).PreviewAsync(request, cancellation.Token));

        using var applyCancellation = new CancellationTokenSource();
        var applyRunner = new FakeRunner((_, arguments, token) =>
        {
            if (arguments.Contains("--dry-run"))
                return Task.FromResult(new ProcessResult(0,
                    "{\"success\":true,\"actions\":{\"LINK\":[{\"name\":\"numpy\",\"version\":\"1.0\"}]}}", ""));
            applyCancellation.Cancel();
            return Task.FromCanceled<ProcessResult>(token);
        });
        var service = new CondaPackageOperator(applyRunner);
        var preview = await service.PreviewAsync(request, CancellationToken.None);
        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            service.ApplyAsync(request, preview, applyCancellation.Token));
    }

    private static PackageOperationRequest Request(DiscoveryChecks.DiscoveryTree tree,
        PackageAction action, string name, string? version,
        CondaPackage? installed = null)
    {
        var installation = Installation(tree);
        var prefix = tree.CreateDirectory("envs", "package-target");
        MakeEnvironment(prefix);
        return new PackageOperationRequest(installation, prefix, action, name, version, installed, []);
    }

    private static VerifiedInstallation Installation(DiscoveryChecks.DiscoveryTree tree)
    {
        var root = tree.CreateRoot("base-conda");
        return new VerifiedInstallation(new CondaInfo(root, "25.5.1", []),
            new CondaLauncher(Path.Combine(root, "python.exe"), ["-m", "conda"]));
    }

    private static void MakeEnvironment(string prefix)
    {
        var marker = Path.Combine(prefix, "conda-meta", "history");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "# disposable test environment");
    }

    private sealed record Call(CondaLauncher Launcher, IReadOnlyList<string> Arguments, TimeSpan Timeout);

    private sealed class FakeRunner(
        Func<CondaLauncher, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> response)
        : IProcessRunner
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
