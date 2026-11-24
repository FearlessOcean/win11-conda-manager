namespace CondaManager.Core;

public static class WindowsPathSafety
{
    public static bool IsUnsupportedAlias(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var normalizedSeparators = path.Replace('/', '\\');
        if (normalizedSeparators.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            normalizedSeparators.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var segment in normalizedSeparators.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = segment.Split('.')[0];
            var tilde = stem.LastIndexOf('~');
            if (tilde >= 0 && tilde < stem.Length - 1 &&
                stem[(tilde + 1)..].All(char.IsAsciiDigit))
                return true;
        }
        return false;
    }
}
