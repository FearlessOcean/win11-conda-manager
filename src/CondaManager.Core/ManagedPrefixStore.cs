using System.Security;
using System.Text.Json;

namespace CondaManager.Core;

public interface IManagedPrefixStore
{
    IReadOnlyList<string> Load();
    void Add(string prefix);
    void Remove(string prefix);
}

public sealed class ManagedPrefixStore : IManagedPrefixStore
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private HashSet<string>? _prefixes;

    public ManagedPrefixStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CondaManager", "known-prefixes.json");
        if (string.IsNullOrWhiteSpace(_filePath) || !Path.IsPathFullyQualified(_filePath))
            throw new ArgumentException("设置文件必须是绝对路径。", nameof(filePath));
        _filePath = Path.GetFullPath(_filePath);
    }

    public IReadOnlyList<string> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _prefixes!.OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void Add(string prefix)
    {
        var normalized = NormalizePrefix(prefix);
        lock (_gate)
        {
            EnsureLoaded();
            if (_prefixes!.Add(normalized))
                Persist();
        }
    }

    public void Remove(string prefix)
    {
        var normalized = NormalizePrefix(prefix);
        lock (_gate)
        {
            EnsureLoaded();
            if (_prefixes!.Remove(normalized))
                Persist();
        }
    }

    private void EnsureLoaded()
    {
        if (_prefixes is not null)
            return;
        _prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_filePath))
                return;
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("prefixes", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && TryNormalizePrefix(entry.GetString(), out var prefix))
                    _prefixes.Add(prefix);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          SecurityException or JsonException or ArgumentException or NotSupportedException)
        {
            _prefixes.Clear();
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new
            {
                prefixes = _prefixes!.OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase).ToArray()
            }));
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
                // Keep the original persistence failure if temporary cleanup also fails.
            }
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        if (!TryNormalizePrefix(prefix, out var normalized))
            throw new ArgumentException("环境路径必须是有效的绝对路径。", nameof(prefix));
        return normalized;
    }

    private static bool TryNormalizePrefix(string? prefix, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(prefix) || !Path.IsPathFullyQualified(prefix))
            return false;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
