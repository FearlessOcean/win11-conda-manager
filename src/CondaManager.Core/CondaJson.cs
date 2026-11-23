using System.Text.Json;

namespace CondaManager.Core;

public static class CondaJson
{
    public static CondaInfo ParseInfo(string json)
    {
        using var document = ParseDocument(json, "Conda info");
        var root = RequireObject(document.RootElement, "Conda info");
        var rootPrefix = RequireAbsolutePath(RequiredString(root, "root_prefix"), "root_prefix");
        var condaVersion = RequiredString(root, "conda_version");
        var envsDirs = ParsePaths(RequiredArray(root, "envs_dirs"), "envs_dirs");

        return new CondaInfo(rootPrefix, condaVersion, envsDirs);
    }

    public static IReadOnlyList<string> ParseEnvironmentPrefixes(string json)
    {
        using var document = ParseDocument(json, "Conda env list");
        var root = RequireObject(document.RootElement, "Conda env list");
        return ParsePaths(RequiredArray(root, "envs"), "envs");
    }

    public static IReadOnlyList<CondaPackage> ParsePackages(string json)
    {
        using var document = ParseDocument(json, "Conda package list");
        var root = RequireArray(document.RootElement, "Conda package list");
        var packages = new List<CondaPackage>();

        foreach (var entry in root.EnumerateArray())
        {
            var item = RequireObject(entry, "Package entry");
            packages.Add(new CondaPackage(
                RequiredString(item, "name"),
                RequiredString(item, "version"),
                OptionalString(item, "build_string"),
                OptionalString(item, "channel")));
        }

        return packages;
    }

    private static JsonDocument ParseDocument(string json, string context)
    {
        if (json is null)
            throw new CondaDataException($"{context} JSON is null.");

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new CondaDataException($"{context} JSON is malformed.", exception);
        }
    }

    private static JsonElement RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new CondaDataException($"{context} must be a JSON object.");
        return element;
    }

    private static JsonElement RequireArray(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new CondaDataException($"{context} must be a JSON array.");
        return element;
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
            throw new CondaDataException($"Required field '{propertyName}' is missing.");
        return RequireArray(property, $"Field '{propertyName}'");
    }

    private static string RequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
            throw new CondaDataException($"Required field '{propertyName}' is missing.");
        if (property.ValueKind != JsonValueKind.String)
            throw new CondaDataException($"Field '{propertyName}' must be a string.");

        var value = property.GetString()!;
        if (string.IsNullOrWhiteSpace(value))
            throw new CondaDataException($"Field '{propertyName}' must not be empty.");
        return value;
    }

    private static string? OptionalString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new CondaDataException($"Field '{propertyName}' must be a string.");
        return property.GetString();
    }

    private static IReadOnlyList<string> ParsePaths(JsonElement array, string fieldName)
    {
        var paths = new List<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                throw new CondaDataException($"Every path in '{fieldName}' must be a string.");
            paths.Add(RequireAbsolutePath(entry.GetString()!, fieldName));
        }
        return paths;
    }

    private static string RequireAbsolutePath(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new CondaDataException($"Field '{fieldName}' requires an absolute path.");

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CondaDataException($"Field '{fieldName}' contains an invalid path.", exception);
        }
    }
}
