using System.Text.Json;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class CatalogChecks
{
    [Check("environment catalog uses verified launcher and keeps one entry per full prefix")]
    public static async Task ListsNormalizedEnvironments()
    {
        var installation = Installation();
        var custom = Path.Combine(Path.GetTempPath(), "custom env with spaces", "分析 环境");
        var runner = new FakeRunner(new ProcessResult(0,
            JsonSerializer.Serialize(new { envs = new[] { custom, custom.ToUpperInvariant() + Path.DirectorySeparatorChar,
                installation.Info.RootPrefix + Path.DirectorySeparatorChar } }), ""));

        var environments = await new CondaEnvironmentCatalog(runner)
            .ListEnvironmentsAsync(installation, CancellationToken.None);

        Expect.Equal(2, environments.Count);
        Expect.Equal(custom, environments[0].Prefix);
        Expect.Equal("分析 环境", environments[0].DisplayName);
        Expect.True(!environments[0].IsBase, "The custom prefix must not be marked base.");
        Expect.Equal(installation.Info.RootPrefix, environments[1].Prefix);
        Expect.Equal("base", environments[1].DisplayName);
        Expect.True(environments[1].IsBase, "The verified root must be marked base.");
        Expect.Equal(1, runner.Calls.Count);
        Expect.True(ReferenceEquals(installation.Launcher, runner.Calls[0].Launcher),
            "The catalog must use the verified launcher, including its leading arguments.");
        Expect.Equal("env", runner.Calls[0].Arguments[0]);
        Expect.Equal("list", runner.Calls[0].Arguments[1]);
        Expect.Equal("--json", runner.Calls[0].Arguments[2]);
        Expect.Equal(3, runner.Calls[0].Arguments.Count);
        Expect.True(runner.Calls[0].Timeout > TimeSpan.Zero, "The environment query must be bounded.");
    }

    [Check("environment catalog adds verified base when Conda omits it")]
    public static async Task AddsOmittedBase()
    {
        var installation = Installation();
        var custom = Path.Combine(Path.GetTempPath(), "isolated", "work");
        var runner = new FakeRunner(new ProcessResult(0,
            JsonSerializer.Serialize(new { envs = new[] { custom }, envs_details = new { } }), ""));

        var environments = await new CondaEnvironmentCatalog(runner)
            .ListEnvironmentsAsync(installation, CancellationToken.None);

        Expect.Equal(2, environments.Count);
        Expect.Equal(installation.Info.RootPrefix, environments[0].Prefix);
        Expect.True(environments[0].IsBase, "The base installation must be included when omitted.");
        Expect.Equal(custom, environments[1].Prefix);
    }

    [Check("environment catalog lists packages for a custom absolute prefix")]
    public static async Task ListsSelectedPrefixPackages()
    {
        var installation = Installation();
        var prefix = Path.Combine(Path.GetTempPath(), "custom prefix", "项目 环境");
        var runner = new FakeRunner(new ProcessResult(0,
            "[{\"name\":\"python\",\"version\":\"3.12.6\",\"build_string\":\"h123\",\"channel\":\"conda-forge\"}," +
            "{\"name\":\"local\",\"version\":\"1.0\"}]", ""));

        var packages = await new CondaEnvironmentCatalog(runner)
            .ListPackagesAsync(installation, prefix + Path.DirectorySeparatorChar, CancellationToken.None);

        Expect.Equal(2, packages.Count);
        Expect.Equal("python", packages[0].Name);
        Expect.Equal("3.12.6", packages[0].Version);
        Expect.Equal("h123", packages[0].Build);
        Expect.Equal("conda-forge", packages[0].Channel);
        Expect.Equal(1, runner.Calls.Count);
        Expect.Equal("list", runner.Calls[0].Arguments[0]);
        Expect.Equal("--prefix", runner.Calls[0].Arguments[1]);
        Expect.Equal(prefix, runner.Calls[0].Arguments[2]);
        Expect.Equal("--json", runner.Calls[0].Arguments[3]);
        Expect.Equal(4, runner.Calls[0].Arguments.Count);
        Expect.True(runner.Calls[0].Timeout >= TimeSpan.FromMinutes(1),
            "Package listing needs enough time for Conda's pip scan.");
    }

    [Check("environment catalog rejects invalid prefix before launching Conda")]
    public static async Task RejectsInvalidPrefix()
    {
        var runner = new FakeRunner();
        await Expect.ThrowsAsync<ArgumentException>(() =>
            new CondaEnvironmentCatalog(runner).ListPackagesAsync(Installation(), "relative\\env", CancellationToken.None));
        Expect.Equal(0, runner.Calls.Count);
    }

    [Check("environment catalog checks exit status before parsing and keeps output private")]
    public static async Task ExitFailureDoesNotExposeOutput()
    {
        var installation = Installation();
        const string privateOutput = "PRIVATE_PACKAGE_CONTENT";
        var runner = new FakeRunner(new ProcessResult(18, "{\"error\":\"PRIVATE_PACKAGE_CONTENT\"}", privateOutput));

        var failure = await Expect.ThrowsAsync<CondaDataException>(() =>
            new CondaEnvironmentCatalog(runner).ListEnvironmentsAsync(installation, CancellationToken.None));

        Expect.True(failure.Message.Contains("18", StringComparison.Ordinal),
            "The command exit code must appear in the diagnostic.");
        Expect.True(!failure.Message.Contains(privateOutput, StringComparison.Ordinal),
            "Raw Conda output may contain package or machine details and must stay private.");
    }

    [Check("environment catalog rejects malformed successful JSON")]
    public static async Task RejectsMalformedSuccess()
    {
        var runner = new FakeRunner(new ProcessResult(0, "{", ""));
        await Expect.ThrowsAsync<CondaDataException>(() =>
            new CondaEnvironmentCatalog(runner).ListPackagesAsync(
                Installation(), Path.Combine(Path.GetTempPath(), "an env"), CancellationToken.None));
    }

    [Check("environment catalog propagates cancellation")]
    public static async Task PropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        var runner = new FakeRunner(new ProcessResult(0, "{\"envs\":[]}", ""));
        runner.OnRun = () => source.Cancel();

        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            new CondaEnvironmentCatalog(runner).ListEnvironmentsAsync(Installation(), source.Token));
    }

    private static VerifiedInstallation Installation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Conda root with spaces", "大版本");
        return new VerifiedInstallation(new CondaInfo(root, "26.10", []),
            new CondaLauncher(Path.Combine(root, "python.exe"), ["-m", "conda"]));
    }

    private sealed record Call(CondaLauncher Launcher, IReadOnlyList<string> Arguments, TimeSpan Timeout);

    private sealed class FakeRunner(params ProcessResult[] responses) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _responses = new(responses);
        public List<Call> Calls { get; } = [];
        public Action? OnRun { get; set; }

        public Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(launcher, arguments.ToArray(), timeout));
            OnRun?.Invoke();
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
