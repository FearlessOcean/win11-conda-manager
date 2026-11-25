using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CondaManager.Core;

public sealed record CondaLauncher(string ExecutablePath, IReadOnlyList<string> LeadingArguments)
{
    public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; init; } =
        new Dictionary<string, string>();

    public int? MaxStdoutBytes { get; init; }
    public int? MaxStderrBytes { get; init; }
}

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
        ArgumentNullException.ThrowIfNull(launcher.EnvironmentOverrides);
        if (launcher.MaxStdoutBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(launcher.MaxStdoutBytes));
        if (launcher.MaxStderrBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(launcher.MaxStderrBytes));
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
        foreach (var (name, value) in launcher.EnvironmentOverrides)
            startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            linkedCancellation.CancelAfter(timeout);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start process '{launcher.ExecutablePath}'.");

        var stdoutTask = ReadOutputAsync(process.StandardOutput, launcher.MaxStdoutBytes, linkedCancellation.Token);
        var stderrTask = ReadOutputAsync(process.StandardError, launcher.MaxStderrBytes, linkedCancellation.Token);
        var exitTask = process.WaitForExitAsync(linkedCancellation.Token);

        try
        {
            // Observe each reader as it finishes: waiting for process exit first can deadlock
            // when an over-limit child is blocked writing to a full pipe.
            var pending = new List<Task> { stdoutTask, stderrTask, exitTask };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            var cleanupFailure = await StopProcessAsync(process).ConfigureAwait(false);
            var cleanupDiagnostic = cleanupFailure is null ? "" : $" Cleanup failed: {cleanupFailure.Message}";
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(
                    $"Process '{launcher.ExecutablePath}' was canceled.{cleanupDiagnostic}", cleanupFailure, cancellationToken);
            throw new TimeoutException(
                $"Process '{launcher.ExecutablePath}' timed out after {timeout}.{cleanupDiagnostic}", cleanupFailure);
        }
        catch (IOException)
        {
            linkedCancellation.Cancel();
            await StopProcessAsync(process).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, int? maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes is null)
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var output = new StringBuilder();
        var buffer = new char[4096];
        var bytesRead = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return output.ToString();

            var chunkBytes = Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
            if (chunkBytes > maxBytes.Value - bytesRead)
                throw new IOException("Process output exceeded the configured size limit.");
            bytesRead += chunkBytes;
            output.Append(buffer, 0, count);
        }
    }

    private static async Task<Exception?> StopProcessAsync(Process process)
    {
        Exception? cleanupFailure = null;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            cleanupFailure = exception;
        }

        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await process.WaitForExitAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cleanupCancellation.IsCancellationRequested)
        {
            var waitFailure = new TimeoutException("Process cleanup did not finish within one second.", exception);
            cleanupFailure = cleanupFailure is null ? waitFailure : new AggregateException(cleanupFailure, waitFailure);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            cleanupFailure = cleanupFailure is null ? exception : new AggregateException(cleanupFailure, exception);
        }
        return cleanupFailure;
    }
}
