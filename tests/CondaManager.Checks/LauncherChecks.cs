using System.Text.Json;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class LauncherChecks
{
    [Check("launcher validator prefers Scripts conda.exe and normalizes the reported root")]
    public static async Task PrefersCondaExecutable()
    {
        using var fixture = new CandidateFiles();
        var runner = new FakeRunner();
        runner.Return(ValidInfo(fixture.Root));

        var result = await new CondaLauncherValidator(runner).ValidateAsync(Path.Combine(fixture.Root, "."), CancellationToken.None);

        Expect.True(result.Installation is not null, "Matching Conda info must verify the installation.");
        Expect.Equal(fixture.CondaExecutable, result.Installation!.Launcher.ExecutablePath);
        Expect.Equal(fixture.Root, result.Installation.Info.RootPrefix);
        Expect.Equal(0, result.Installation.Launcher.LeadingArguments.Count);
        Expect.Equal(1, runner.Calls.Count);
        AssertInfoCall(runner.Calls[0]);
        Expect.Equal(0, result.Failures.Count);
    }

    [Check("launcher validator falls back to own python after nonzero conda.exe exit")]
    public static async Task FallsBackAfterNonzeroExit()
    {
        using var fixture = new CandidateFiles();
        var runner = new FakeRunner();
        runner.Return(new ProcessResult(23, "", "broken launcher"));
        runner.Return(ValidInfo(fixture.Root));

        var result = await new CondaLauncherValidator(runner).ValidateAsync(fixture.Root, CancellationToken.None);

        Expect.True(result.Installation is not null, "The Python fallback must verify this root.");
        Expect.Equal(fixture.PythonExecutable, result.Installation!.Launcher.ExecutablePath);
        Expect.Equal(2, result.Installation.Launcher.LeadingArguments.Count);
        Expect.Equal("-m", result.Installation.Launcher.LeadingArguments[0]);
        Expect.Equal("conda", result.Installation.Launcher.LeadingArguments[1]);
        Expect.Equal(2, runner.Calls.Count);
        AssertInfoCall(runner.Calls[0]);
        AssertInfoCall(runner.Calls[1]);
        Expect.True(result.Failures.Any(failure => failure.Contains("23", StringComparison.Ordinal)),
            "The failed first launcher must remain in diagnostics.");
    }

    [Check("launcher validator falls back after start failure, timeout, malformed JSON, or mismatched root")]
    public static async Task FallsBackAfterInvalidCandidate()
    {
        foreach (var failedFirst in new Func<string, FakeRunner, string>[]
        {
            (_, runner) => { runner.Throw(new System.ComponentModel.Win32Exception("cannot start")); return "start"; },
            (_, runner) => { runner.Throw(new TimeoutException("deadline")); return "timeout"; },
            (_, runner) => { runner.Return(new ProcessResult(0, "{", "")); return "JSON"; },
            (root, runner) => { runner.Return(ValidInfo(Path.Combine(root, "other"))); return "root"; }
        })
        {
            using var fixture = new CandidateFiles();
            var runner = new FakeRunner();
            var expectedDiagnostic = failedFirst(fixture.Root, runner);
            runner.Return(ValidInfo(fixture.Root));

            var result = await new CondaLauncherValidator(runner).ValidateAsync(fixture.Root, CancellationToken.None);

            Expect.True(result.Installation is not null, $"Fallback failed after {expectedDiagnostic} failure.");
            Expect.Equal(fixture.PythonExecutable, result.Installation!.Launcher.ExecutablePath);
            Expect.Equal(2, runner.Calls.Count);
            Expect.True(result.Failures.Any(failure => failure.Contains(expectedDiagnostic, StringComparison.OrdinalIgnoreCase)),
                $"Missing diagnostic for {expectedDiagnostic} failure.");
        }
    }

    [Check("launcher validator rejects both candidates when neither reports the requested root")]
    public static async Task RejectsMismatchedRoots()
    {
        using var fixture = new CandidateFiles();
        var runner = new FakeRunner();
        runner.Return(ValidInfo(Path.Combine(fixture.Root, "foreign")));
        runner.Return(ValidInfo(Path.Combine(fixture.Root, "foreign")));

        var result = await new CondaLauncherValidator(runner).ValidateAsync(fixture.Root, CancellationToken.None);

        Expect.True(result.Installation is null, "A candidate reporting a different root cannot be accepted.");
        Expect.Equal(2, runner.Calls.Count);
        Expect.Equal(2, result.Failures.Count);
    }

    [Check("launcher validator skips absent candidate files")]
    public static async Task SkipsAbsentCandidates()
    {
        using var fixture = new CandidateFiles(createCondaExecutable: false, createPythonExecutable: false);
        var runner = new FakeRunner();

        var result = await new CondaLauncherValidator(runner).ValidateAsync(fixture.Root, CancellationToken.None);

        Expect.True(result.Installation is null, "A missing executable cannot verify an installation.");
        Expect.Equal(0, runner.Calls.Count);
        Expect.Equal(2, result.Failures.Count);
    }

    [Check("launcher validator stops immediately on user cancellation")]
    public static async Task StopsOnUserCancellation()
    {
        using var fixture = new CandidateFiles();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner();
        runner.Cancel(cancellation);

        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            new CondaLauncherValidator(runner).ValidateAsync(fixture.Root, cancellation.Token));

        Expect.Equal(1, runner.Calls.Count);
    }

    private static ProcessResult ValidInfo(string root) => new(0,
        JsonSerializer.Serialize(new { root_prefix = root, conda_version = "25.1", envs_dirs = Array.Empty<string>() }), "");

    private static void AssertInfoCall(Call call)
    {
        Expect.Equal(2, call.Arguments.Count);
        Expect.Equal("info", call.Arguments[0]);
        Expect.Equal("--json", call.Arguments[1]);
        Expect.Equal(TimeSpan.FromSeconds(30), call.Timeout);
    }

    private sealed class CandidateFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, $"conda 根 with spaces-{Guid.NewGuid():N}");
        public string CondaExecutable => Path.Combine(Root, "Scripts", "conda.exe");
        public string PythonExecutable => Path.Combine(Root, "python.exe");

        public CandidateFiles(bool createCondaExecutable = true, bool createPythonExecutable = true)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Scripts"));
            if (createCondaExecutable)
                File.WriteAllText(CondaExecutable, "");
            if (createPythonExecutable)
                File.WriteAllText(PythonExecutable, "");
        }

        public void Dispose()
        {
            var root = Path.GetFullPath(Root);
            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            if (!root.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Test cleanup path escaped the test output directory.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Call(CondaLauncher Launcher, IReadOnlyList<string> Arguments, TimeSpan Timeout);

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Queue<Func<CancellationToken, Task<ProcessResult>>> _responses = new();
        public List<Call> Calls { get; } = [];

        public void Return(ProcessResult result) => _responses.Enqueue(_ => Task.FromResult(result));
        public void Throw(Exception exception) => _responses.Enqueue(_ => Task.FromException<ProcessResult>(exception));
        public void Cancel(CancellationTokenSource source) => _responses.Enqueue(token =>
        {
            source.Cancel();
            return Task.FromCanceled<ProcessResult>(token);
        });

        public Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(launcher, arguments.ToArray(), timeout));
            return _responses.Dequeue()(cancellationToken);
        }
    }
}
