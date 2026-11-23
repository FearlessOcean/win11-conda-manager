using System.Security;
using System.Text.Json;

namespace CondaManager.Core;

public sealed class SelectionSettings
{
    private readonly string _filePath;

    public SelectionSettings(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CondaManager", "settings.json");
        ArgumentException.ThrowIfNullOrWhiteSpace(_filePath);
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("selectedRoot", out var property) ||
                property.ValueKind != JsonValueKind.String)
                return null;

            var root = property.GetString();
            return NormalizeRoot(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          SecurityException or JsonException or ArgumentException or
                                          NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string root)
    {
        var normalized = NormalizeRoot(root) ??
            throw new ArgumentException("Selected root must be an absolute path.", nameof(root));
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Settings file must have a parent directory.", nameof(_filePath));

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { selectedRoot = normalized }));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original write failure; a leftover temporary file is harmless.
            }
        }
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
