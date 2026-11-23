namespace CondaManager.Core;

public sealed record CondaInfo(
    string RootPrefix,
    string CondaVersion,
    IReadOnlyList<string> EnvsDirs);

public sealed record CondaPackage(
    string Name,
    string Version,
    string? Build,
    string? Channel);

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
