using CondaManager.Core;

namespace CondaManager.Checks;

public static class ManagedPrefixChecks
{
    [Check("managed prefixes persist and deduplicate normalized paths without case")]
    public static void RoundTripAndDeduplicate()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var prefix = tree.CreateDirectory("custom", "分析环境");
        var file = Path.Combine(tree.Base, "settings", "known-prefixes.json");
        var store = new ManagedPrefixStore(file);

        store.Add(prefix + Path.DirectorySeparatorChar);
        store.Add(prefix.ToUpperInvariant());
        Expect.Equal(1, store.Load().Count);
        Expect.Equal(prefix, store.Load()[0]);

        var reopened = new ManagedPrefixStore(file);
        Expect.Equal(prefix, reopened.Load().Single());
        reopened.Remove(prefix.ToUpperInvariant());
        Expect.Equal(0, new ManagedPrefixStore(file).Load().Count);
    }

    [Check("managed prefix store tolerates malformed settings and rewrites them")]
    public static void MalformedSettingsDoNotBlockAdd()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var prefix = tree.CreateDirectory("custom", "science");
        var file = Path.Combine(tree.Base, "known-prefixes.json");
        File.WriteAllText(file, "{");
        var store = new ManagedPrefixStore(file);

        Expect.Equal(0, store.Load().Count);
        store.Add(prefix);
        Expect.Equal(prefix, new ManagedPrefixStore(file).Load().Single());
    }

    [Check("managed prefix store keeps a session entry if disk persistence fails")]
    public static void WriteFailureRetainsSessionEntry()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var prefix = tree.CreateDirectory("custom", "science");
        var pathOccupiedByDirectory = tree.CreateDirectory("settings-path-is-directory");
        var store = new ManagedPrefixStore(pathOccupiedByDirectory);

        Exception? failure = null;
        try
        {
            store.Add(prefix);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception;
        }
        Expect.True(failure is not null, "A failed settings write must be reported to the caller.");
        Expect.Equal(prefix, store.Load().Single());
    }

    [Check("managed prefix store refuses relative paths before writing")]
    public static void RejectsRelativePath()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var file = Path.Combine(tree.Base, "known-prefixes.json");
        var store = new ManagedPrefixStore(file);

        Expect.Throws<ArgumentException>(() => store.Add("relative\\environment"));
        Expect.Equal(0, store.Load().Count);
        Expect.True(!File.Exists(file), "An invalid path must not create settings.");
    }
}
