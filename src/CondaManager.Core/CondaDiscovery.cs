using System.ComponentModel;

namespace CondaManager.Core;

public sealed record VerifiedInstallation(CondaInfo Info, CondaLauncher Launcher);

public sealed record LauncherValidationResult(VerifiedInstallation? Installation, IReadOnlyList<string> Failures);

public sealed class CondaLauncherValidator(IProcessRunner runner)
{
    public async Task<LauncherValidationResult> ValidateAsync(string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return new LauncherValidationResult(null, ["Installation root must be an absolute path."]);

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LauncherValidationResult(null, [$"Installation root is invalid: {exception.Message}"]);
        }

        var failures = new List<string>();
        var candidates = new[]
        {
            new CondaLauncher(Path.Combine(normalizedRoot, "Scripts", "conda.exe"), []),
            new CondaLauncher(Path.Combine(normalizedRoot, "python.exe"), ["-m", "conda"])
        };

        foreach (var launcher in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(launcher.ExecutablePath))
            {
                failures.Add($"Launcher file is missing: {launcher.ExecutablePath}");
                continue;
            }

            ProcessResult processResult;
            try
            {
                processResult = await runner.RunAsync(launcher, ["info", "--json"], TimeSpan.FromSeconds(30), ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                failures.Add($"Launcher {launcher.ExecutablePath} failed due to timeout: {exception.Message}");
                continue;
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add($"Launcher {launcher.ExecutablePath} could not start: {exception.Message}");
                continue;
            }

            if (processResult.ExitCode != 0)
            {
                failures.Add($"Launcher {launcher.ExecutablePath} returned exit code {processResult.ExitCode}: {processResult.Stderr}");
                continue;
            }

            CondaInfo info;
            try
            {
                info = CondaJson.ParseInfo(processResult.Stdout);
            }
            catch (CondaDataException exception)
            {
                failures.Add($"Launcher {launcher.ExecutablePath} returned invalid Conda JSON: {exception.Message}");
                continue;
            }

            if (!string.Equals(info.RootPrefix, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Launcher {launcher.ExecutablePath} reported root '{info.RootPrefix}' instead of '{normalizedRoot}'.");
                continue;
            }

            return new LauncherValidationResult(new VerifiedInstallation(info, launcher), failures);
        }

        return new LauncherValidationResult(null, failures);
    }
}
