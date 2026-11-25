using System.Diagnostics;
using System.Text.Json;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class ProcessChecks
{
    [Check("process runner applies launcher environment overrides only to the child")]
    public static async Task LauncherEnvironmentOverrides()
    {
        var variable = $"CONDA_MANAGER_CHECK_{Guid.NewGuid():N}";
        var launcher = TestLauncher("--echo-env") with
        {
            EnvironmentOverrides = new Dictionary<string, string> { [variable] = "child-only" }
        };

        var result = await new ProcessRunner().RunAsync(launcher,
            [variable], TimeSpan.FromSeconds(10), CancellationToken.None);

        Expect.Equal(0, result.ExitCode);
        Expect.Equal("child-only", result.Stdout);
        Expect.True(Environment.GetEnvironmentVariable(variable) is null,
            "The parent process environment must remain unchanged.");
    }

    [Check("process runner preserves separate arguments, including spaces and Chinese")]
    public static async Task PreservesArguments()
    {
        var launcher = TestLauncher("--echo-args");
        var arguments = new[] { @"C:\Program Files\Miniconda 根\envs\my env", "--prefix", "含 空格", "quoted \"value\"" };

        var result = await new ProcessRunner().RunAsync(launcher, arguments, TimeSpan.FromSeconds(10), CancellationToken.None);

        Expect.Equal(0, result.ExitCode);
        Expect.Equal("", result.Stderr);
        var actual = JsonSerializer.Deserialize<string[]>(result.Stdout);
        Expect.True(actual is not null, "The test helper must return a JSON argument array.");
        Expect.Equal(arguments.Length, actual!.Length);
        for (var index = 0; index < arguments.Length; index++)
            Expect.Equal(arguments[index], actual[index]);
    }

    [Check("process runner retains nonzero exit status and stderr")]
    public static async Task RetainsFailureDetails()
    {
        var result = await new ProcessRunner().RunAsync(
            TestLauncher("--exit"), ["7"], TimeSpan.FromSeconds(10), CancellationToken.None);

        Expect.Equal(7, result.ExitCode);
        Expect.Equal("requested exit", result.Stderr);
    }

    [Check("process runner times out a sleeping child")]
    public static async Task TimesOutSleepingChild()
    {
        var timer = Stopwatch.StartNew();
        await Expect.ThrowsAsync<TimeoutException>(() => new ProcessRunner().RunAsync(
            TestLauncher("--sleep"), ["5000"], TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Expect.True(timer.Elapsed < TimeSpan.FromSeconds(2),
            $"Process timeout took {timer.Elapsed}, exceeding the cleanup bound.");
    }

    [Check("process runner honors user cancellation")]
    public static async Task HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var timer = Stopwatch.StartNew();
        var exception = await Expect.ThrowsAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            TestLauncher("--sleep"), ["5000"], TimeSpan.FromSeconds(10), cancellation.Token));
        Expect.Equal(cancellation.Token, exception.CancellationToken);
        Expect.True(timer.Elapsed < TimeSpan.FromSeconds(2),
            $"Process cancellation took {timer.Elapsed}, exceeding the cleanup bound.");
    }

    [Check("process runner stops an oversized stdout producer during capture")]
    public static async Task BoundsStdoutWhileReading()
    {
        var launcher = WithOutputLimit(TestLauncher("--emit-bytes"), "MaxStdoutBytes", 8 * 1024 * 1024);
        var timer = Stopwatch.StartNew();
        await Expect.ThrowsAsync<IOException>(() => new ProcessRunner().RunAsync(launcher,
            ["stdout", (16 * 1024 * 1024).ToString(), "5000"], TimeSpan.FromSeconds(10), CancellationToken.None));
        Expect.True(timer.Elapsed < TimeSpan.FromSeconds(4),
            $"Oversized stdout was not stopped promptly ({timer.Elapsed}).");
    }

    [Check("process runner stops an oversized stderr producer during capture")]
    public static async Task BoundsStderrWhileReading()
    {
        var launcher = WithOutputLimit(TestLauncher("--emit-bytes"), "MaxStderrBytes", 1024 * 1024);
        var timer = Stopwatch.StartNew();
        await Expect.ThrowsAsync<IOException>(() => new ProcessRunner().RunAsync(launcher,
            ["stderr", (2 * 1024 * 1024).ToString(), "5000"], TimeSpan.FromSeconds(10), CancellationToken.None));
        Expect.True(timer.Elapsed < TimeSpan.FromSeconds(4),
            $"Oversized stderr was not stopped promptly ({timer.Elapsed}).");
    }

    private static CondaLauncher WithOutputLimit(CondaLauncher launcher, string propertyName, int limit)
    {
        var property = typeof(CondaLauncher).GetProperty(propertyName);
        Expect.True(property is not null, $"CondaLauncher must define {propertyName}.");
        property!.SetValue(launcher, limit);
        return launcher;
    }

    private static CondaLauncher TestLauncher(string mode)
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Test process executable path is unavailable.");
        var leading = Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(Program).Assembly.Location, mode }
            : new[] { mode };
        return new CondaLauncher(executable, leading);
    }
}
