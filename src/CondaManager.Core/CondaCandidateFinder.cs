namespace CondaManager.Core;

public static class CondaCandidateFinder
{
    private static readonly string[] ConventionalNames =
        ["anaconda3", "miniconda3", "miniforge3", "mambaforge"];

    public static IReadOnlyList<string> FindRoots(string? savedRoot, string? condaExe,
        string? pathValue, string userProfile, string programData)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (TryNormalize(path, out var root) && seen.Add(root))
                roots.Add(root);
        }

        Add(savedRoot);

        if (TryNormalize(condaExe, out var executable) &&
            IsCondaFileName(Path.GetFileName(executable)))
            Add(RootForLauncherDirectory(Path.GetDirectoryName(executable)!));

        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var rawEntry in pathValue.Split(Path.PathSeparator))
            {
                if (!TryNormalize(rawEntry.Trim().Trim('"'), out var entry))
                    continue;

                if (File.Exists(Path.Combine(entry, "conda.exe")) ||
                    File.Exists(Path.Combine(entry, "conda.bat")))
                    Add(RootForLauncherDirectory(entry));
                else if (File.Exists(Path.Combine(entry, "python.exe")) &&
                         Directory.Exists(Path.Combine(entry, "conda-meta")))
                    Add(entry);
            }
        }

        foreach (var parent in new[] { userProfile, programData })
        {
            if (!TryNormalize(parent, out var normalizedParent))
                continue;

            foreach (var name in ConventionalNames)
            {
                var candidate = Path.Combine(normalizedParent, name);
                if (Directory.Exists(candidate))
                    Add(candidate);
            }
        }

        return roots;
    }

    private static string RootForLauncherDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return string.Equals(name, "Scripts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "condabin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)!
            : directory;
    }

    private static bool IsCondaFileName(string fileName) =>
        string.Equals(fileName, "conda.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "conda.bat", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
