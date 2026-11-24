namespace CondaManager.Core;

public sealed record CondaEnvironment(string Prefix, string DisplayName, bool IsBase);

public interface ICondaEnvironmentCatalog
{
    Task<IReadOnlyList<CondaEnvironment>> ListEnvironmentsAsync(
        VerifiedInstallation installation, CancellationToken cancellationToken);

    Task<IReadOnlyList<CondaPackage>> ListPackagesAsync(
        VerifiedInstallation installation, string prefix, CancellationToken cancellationToken);
}

public sealed class CondaEnvironmentCatalog : ICondaEnvironmentCatalog
{
    private static readonly TimeSpan EnvironmentListTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PackageListTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _runner;

    public CondaEnvironmentCatalog(IProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<IReadOnlyList<CondaEnvironment>> ListEnvironmentsAsync(
        VerifiedInstallation installation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _runner.RunAsync(installation.Launcher,
            ["env", "list", "--json"], EnvironmentListTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result, "env list");

        var root = NormalizePrefix(installation.Info.RootPrefix);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environments = new List<CondaEnvironment>();
        foreach (var prefix in CondaJson.ParseEnvironmentPrefixes(result.Stdout))
        {
            if (seen.Add(prefix))
                environments.Add(ToEnvironment(prefix, root));
        }

        if (seen.Add(root))
            environments.Insert(0, ToEnvironment(root, root));

        return environments;
    }

    public async Task<IReadOnlyList<CondaPackage>> ListPackagesAsync(
        VerifiedInstallation installation, string prefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var normalizedPrefix = NormalizePrefix(prefix);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _runner.RunAsync(installation.Launcher,
            ["list", "--prefix", normalizedPrefix, "--json"], PackageListTimeout, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result, "list");
        return CondaJson.ParsePackages(result.Stdout);
    }

    private static CondaEnvironment ToEnvironment(string prefix, string root)
    {
        var isBase = string.Equals(prefix, root, StringComparison.OrdinalIgnoreCase);
        var name = isBase ? "base" : Path.GetFileName(prefix);
        return new CondaEnvironment(prefix, string.IsNullOrWhiteSpace(name) ? prefix : name, isBase);
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !Path.IsPathFullyQualified(prefix))
            throw new ArgumentException("Conda 环境路径必须是绝对路径。", nameof(prefix));

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Conda 环境路径无效。", nameof(prefix), exception);
        }
    }

    private static void EnsureSuccess(ProcessResult result, string command)
    {
        if (result.ExitCode != 0)
            throw new CondaDataException($"Conda {command} 命令失败（退出码 {result.ExitCode}）。");
    }
}
