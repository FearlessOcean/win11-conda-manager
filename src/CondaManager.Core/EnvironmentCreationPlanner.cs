using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace CondaManager.Core;

public sealed record EnvironmentCreationPlan(
    string Name,
    string ParentDirectory,
    string Prefix,
    string? PythonVersion);

public interface IWritableDirectoryProbe
{
    bool CanCreateChild(string directory);
}

public sealed class WritableDirectoryProbe : IWritableDirectoryProbe
{
    public bool CanCreateChild(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            return false;

        try
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            while (!Directory.Exists(current))
            {
                if (File.Exists(current))
                    return false;
                var parent = Directory.GetParent(current);
                if (parent is null)
                    return false;
                current = parent.FullName;
            }

            var probePath = Path.Combine(current, $".conda-manager-probe-{Guid.NewGuid():N}");
            var created = false;
            try
            {
                Directory.CreateDirectory(probePath);
                created = true;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                return false;
            }

            try
            {
                Directory.Delete(probePath);
                return created;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                return false;
            }
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException;
}

public static class EnvironmentCreationPlanner
{
    private static readonly HashSet<string> ReservedDeviceNames =
        new(["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
             "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly Regex PythonVersionPattern =
        new(@"^[0-9]+\.[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string? ChooseDefaultParent(CondaInfo info, IWritableDirectoryProbe probe)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(info.EnvsDirs);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in info.EnvsDirs)
        {
            if (!TryNormalizeDirectory(candidate, out var normalized) || !seen.Add(normalized))
                continue;
            if (probe.CanCreateChild(normalized))
                return normalized;
        }
        return null;
    }

    public static EnvironmentCreationPlan Build(string name, string parent, string? pythonVersion)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            ReservedDeviceNames.Contains(name) ||
            !name.EnumerateRunes().All(rune => Rune.IsLetterOrDigit(rune) || rune.Value is '_' or '-'))
            throw new ArgumentException("环境名称须为 1–64 个字母、数字、下划线或连字符，且不能是 Windows 保留名称。", nameof(name));

        if (!TryNormalizeDirectory(parent, out var normalizedParent) || File.Exists(normalizedParent))
            throw new ArgumentException("存放目录必须是有效的绝对文件夹路径。", nameof(parent));

        var version = string.IsNullOrWhiteSpace(pythonVersion) ? null : pythonVersion.Trim();
        if (version is not null && !PythonVersionPattern.IsMatch(version))
            throw new ArgumentException("Python 版本应填写为 major.minor 或 major.minor.patch。", nameof(pythonVersion));

        var prefix = Path.Combine(normalizedParent, name);
        return new EnvironmentCreationPlan(name, normalizedParent, prefix, version);
    }

    private static bool TryNormalizeDirectory(string? directory, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            WindowsPathSafety.IsUnsupportedAlias(directory))
            return false;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return !WindowsPathSafety.IsUnsupportedAlias(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
