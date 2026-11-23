using System.Text.Json;
using System.Diagnostics;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class DiscoveryChecks
{
    [Check("candidate roots retain saved, CONDA_EXE, PATH, and conventional order")]
    public static void CandidateOrderAndEligibility()
    {
        using var tree = new DiscoveryTree();
        var saved = tree.CreateRoot("saved");
        var fromExe = tree.CreateRoot("from-exe");
        var fromPath = tree.CreateRoot("from-path");
        var fromBareRoot = tree.CreateRoot("from-bare-root");
        var ignored = tree.CreateRoot("ignored");
        var profile = tree.CreateDirectory("profile");
        var programData = tree.CreateDirectory("program-data");
        var profileConda = tree.CreateDirectory("profile", "anaconda3");
        var programConda = tree.CreateDirectory("program-data", "miniforge3");
        tree.Touch(Path.Combine(fromExe, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(fromPath, "condabin", "conda.bat"));
        tree.Touch(Path.Combine(fromBareRoot, "python.exe"));
        Directory.CreateDirectory(Path.Combine(fromBareRoot, "conda-meta"));
        Directory.CreateDirectory(Path.Combine(ignored, "Scripts"));

        var roots = CondaCandidateFinder.FindRoots(saved + Path.DirectorySeparatorChar,
            Path.Combine(fromExe, "Scripts", "conda.exe"),
            string.Join(Path.PathSeparator, new[]
            {
                Path.Combine(ignored, "Scripts"),
                Path.Combine(fromPath, "condabin"),
                fromBareRoot
            }), profile, programData);

        Expect.Equal(6, roots.Count);
        Expect.Equal(saved, roots[0]);
        Expect.Equal(fromExe, roots[1]);
        Expect.Equal(fromPath, roots[2]);
        Expect.Equal(fromBareRoot, roots[3]);
        Expect.Equal(profileConda, roots[4]);
        Expect.Equal(programConda, roots[5]);
    }

    [Check("candidate roots ignore relative paths and deduplicate without case or trailing slash")]
    public static void CandidateNormalizationAndDeduplication()
    {
        using var tree = new DiscoveryTree();
        var root = tree.CreateRoot("duplicate");
        tree.Touch(Path.Combine(root, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(root, "condabin", "conda.bat"));

        var roots = CondaCandidateFinder.FindRoots(root.ToUpperInvariant() + Path.DirectorySeparatorChar,
            Path.Combine(root, "Scripts", "conda.exe"),
            string.Join(Path.PathSeparator, new[]
            {
                "relative-entry", Path.Combine(root, "Scripts"), Path.Combine(root, "condabin")
            }), tree.CreateDirectory("profile"), tree.CreateDirectory("program-data"));

        Expect.Equal(1, roots.Count);
        Expect.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.ToUpperInvariant())), roots[0]);
    }

    [Check("discovery validates roots once, keeps distinct installations and diagnostics, and reports progress")]
    public static async Task DiscoveryDeduplicationAndDiagnostics()
    {
        using var tree = new DiscoveryTree();
        var first = tree.CreateRoot("first");
        var failed = tree.CreateRoot("failed");
        var second = tree.CreateRoot("second");
        tree.Touch(Path.Combine(first, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(failed, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(second, "Scripts", "conda.exe"));
        var runner = new DiscoveryRunner();
        runner.Return(first, ValidInfo(first));
        runner.Return(failed, new ProcessResult(9, "", "broken"));
        runner.Return(second, ValidInfo(second));
        var updates = new List<CondaDiscoveryProgress>();

        var result = await new CondaDiscoveryService(new CondaLauncherValidator(runner))
            .DiscoverAsync([first, first + Path.DirectorySeparatorChar, failed, second],
                new InlineProgress<CondaDiscoveryProgress>(updates.Add), CancellationToken.None);

        Expect.Equal(2, result.Installations.Count);
        Expect.Equal(first, result.Installations[0].Info.RootPrefix);
        Expect.Equal(second, result.Installations[1].Info.RootPrefix);
        Expect.Equal(3, runner.Calls.Count);
        Expect.True(result.Failures.Any(failure => failure.Contains(failed, StringComparison.OrdinalIgnoreCase)
            && failure.Contains("9", StringComparison.Ordinal)), "The failed root and exit code should remain visible.");
        Expect.True(updates.Any(update => update.Root == first && update.Completed == 0),
            "Progress must identify a root before validation starts.");
        Expect.True(updates.Any(update => update.Root == second && update.Completed == 3),
            "Progress must identify completion of the last unique root.");
    }

    [Check("discovery stops before the next root on user cancellation")]
    public static async Task DiscoveryCancellation()
    {
        using var tree = new DiscoveryTree();
        var first = tree.CreateRoot("cancel-first");
        var second = tree.CreateRoot("cancel-second");
        tree.Touch(Path.Combine(first, "Scripts", "conda.exe"));
        tree.Touch(Path.Combine(second, "Scripts", "conda.exe"));
        using var cancellation = new CancellationTokenSource();
        var runner = new DiscoveryRunner();
        runner.Cancel(first, cancellation);

        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            new CondaDiscoveryService(new CondaLauncherValidator(runner))
                .DiscoverAsync([first, second], null, cancellation.Token));

        Expect.Equal(1, runner.Calls.Count);
        Expect.Equal(first, runner.Calls[0]);
    }

    [Check("slow launcher validation does not block the caller")]
    public static async Task SlowValidationDoesNotBlockCaller()
    {
        using var tree = new DiscoveryTree();
        using var gate = new ManualResetEventSlim(false);
        var root = tree.CreateRoot("slow-validation");
        tree.Touch(Path.Combine(root, "Scripts", "conda.exe"));
        var runner = new BlockingRunner(gate, ValidInfo(root));

        var clock = Stopwatch.StartNew();
        var discovery = new CondaDiscoveryService(new CondaLauncherValidator(runner))
            .DiscoverAsync([root]);
        var callDuration = clock.Elapsed;
        gate.Set();
        var result = await discovery;

        Expect.Equal(1, result.Installations.Count);
        Expect.True(callDuration < TimeSpan.FromMilliseconds(500),
            $"Starting discovery blocked the caller for {callDuration}.");
    }

    [Check("cancellation stops waiting for a slow synchronous validation")]
    public static async Task CancellationStopsWaitingForSlowValidation()
    {
        using var tree = new DiscoveryTree();
        using var gate = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var root = tree.CreateRoot("slow-cancel");
        tree.Touch(Path.Combine(root, "Scripts", "conda.exe"));
        var runner = new BlockingRunner(gate, ValidInfo(root));

        var clock = Stopwatch.StartNew();
        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            new CondaDiscoveryService(new CondaLauncherValidator(runner))
                .DiscoverAsync([root], cancellationToken: cancellation.Token));
        var waitDuration = clock.Elapsed;
        gate.Set();

        Expect.True(waitDuration < TimeSpan.FromMilliseconds(500),
            $"Cancellation waited {waitDuration} for synchronous validation.");
    }

    internal static ProcessResult ValidInfo(string root) => new(0,
        JsonSerializer.Serialize(new
        {
            root_prefix = root,
            conda_version = "25.1",
            envs_dirs = Array.Empty<string>()
        }), "");

    internal sealed class DiscoveryTree : IDisposable
    {
        public string Base { get; } = Path.Combine(AppContext.BaseDirectory, $"discovery-测试-{Guid.NewGuid():N}");

        public string CreateDirectory(params string[] parts)
        {
            var path = Path.Combine([Base, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateRoot(string name) => CreateDirectory(name);

        public void Touch(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        public void Dispose()
        {
            var parent = Path.GetFullPath(AppContext.BaseDirectory);
            var target = Path.GetFullPath(Base);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Test cleanup path escaped the test output directory.");
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    internal sealed class DiscoveryRunner : IProcessRunner
    {
        private readonly Dictionary<string, Func<CancellationToken, Task<ProcessResult>>> _responses =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public void Return(string root, ProcessResult result) =>
            _responses.Add(root, _ => Task.FromResult(result));

        public void Cancel(string root, CancellationTokenSource source) =>
            _responses.Add(root, token =>
            {
                source.Cancel();
                return Task.FromCanceled<ProcessResult>(token);
            });

        public Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(launcher.ExecutablePath)!)!;
            Calls.Add(root);
            return _responses[root](cancellationToken);
        }
    }

    private sealed class BlockingRunner(ManualResetEventSlim gate, ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            gate.Wait(TimeSpan.FromSeconds(1));
            return Task.FromResult(result);
        }
    }

    internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
