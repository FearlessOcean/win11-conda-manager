namespace CondaManager.Core;

public sealed record CondaDiscoveryProgress(string Root, int Completed, int Total);

public sealed record CondaDiscoveryResult(
    IReadOnlyList<VerifiedInstallation> Installations,
    IReadOnlyList<string> Failures);

public sealed class CondaDiscoveryService(CondaLauncherValidator validator)
{
    public async Task<CondaDiscoveryResult> DiscoverAsync(IReadOnlyList<string> roots,
        IProgress<CondaDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var uniqueRoots = new List<string>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var key = NormalizeIfAbsolute(root);
            if (seenCandidates.Add(key))
                uniqueRoots.Add(key);
        }

        var installations = new List<VerifiedInstallation>();
        var failures = new List<string>();
        var seenInstallations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < uniqueRoots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = uniqueRoots[index];
            progress?.Report(new CondaDiscoveryProgress(root, index, uniqueRoots.Count));

            var validation = await Task.Run(() => validator.ValidateAsync(root, cancellationToken),
                cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var failure in validation.Failures)
                failures.Add($"{root}: {failure}");

            if (validation.Installation is { } installation &&
                seenInstallations.Add(NormalizeIfAbsolute(installation.Info.RootPrefix)))
                installations.Add(installation);

            progress?.Report(new CondaDiscoveryProgress(root, index + 1, uniqueRoots.Count));
        }

        return new CondaDiscoveryResult(installations, failures);
    }

    private static string NormalizeIfAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return path ?? "";

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
