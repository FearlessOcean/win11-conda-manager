namespace CondaManager.Core;

public sealed record CondaInfo(
    string RootPrefix,
    string CondaVersion,
    IReadOnlyList<string> EnvsDirs);

public enum CondaPackageOrigin
{
    Unknown,
    Conda,
    Pip
}

public sealed record CondaPackage(
    string Name,
    string Version,
    string? Build,
    string? Channel)
{
    public string? PackageType { get; init; }

    public CondaPackageOrigin Origin
    {
        get
        {
            const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            var channel = Channel?.Trim();
            var packageType = PackageType?.Trim();

            if (string.Equals(channel, "pypi", comparison) ||
                string.Equals(channel, "<develop>", comparison) ||
                packageType?.StartsWith("virtual_python_", comparison) == true ||
                string.Equals(Build?.Trim(), "pypi_0", comparison))
                return CondaPackageOrigin.Pip;

            if (string.Equals(packageType, "conda", comparison) ||
                (!string.IsNullOrEmpty(channel) && !string.Equals(channel, "<unknown>", comparison)))
                return CondaPackageOrigin.Conda;

            return CondaPackageOrigin.Unknown;
        }
    }
}

public sealed class CondaDataException : Exception
{
    public CondaDataException(string message) : base(message)
    {
    }

    public CondaDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
