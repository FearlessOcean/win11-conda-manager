using System.ComponentModel;
using System.Diagnostics;

namespace CondaManager.Core;

public sealed record CondaLauncher(string ExecutablePath, IReadOnlyList<string> LeadingArguments);

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(launcher.LeadingArguments);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");

        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(launcher.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in launcher.LeadingArguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            linkedCancellation.CancelAfter(timeout);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start process '{launcher.ExecutablePath}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCancellation.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process may exit between HasExited and Kill.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Process '{launcher.ExecutablePath}' timed out after {timeout}.");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
