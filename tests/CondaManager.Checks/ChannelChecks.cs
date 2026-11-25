using CondaManager.Core;

namespace CondaManager.Checks;

public static class ChannelChecks
{
    [Check("Channel names and HTTPS URLs normalize without changing path casing")]
    public static void NormalizesChannelEntries()
    {
        Expect.Equal("conda-forge", ChannelPolicy.Normalize("conda-forge"));
        Expect.Equal("Build_01.release", ChannelPolicy.Normalize("Build_01.release"));
        Expect.Equal("https://repo.example.org:8443/Channels/Stable",
            ChannelPolicy.Normalize("HTTPS://REPO.Example.org:8443/Channels/Stable///"));
        Expect.Equal("https://repo.example.org", ChannelPolicy.Normalize("https://repo.example.org/"));
    }

    [Check("Channel validation rejects local, ambiguous, and credential-bearing entries")]
    public static void RejectsInvalidChannels()
    {
        foreach (var value in new[]
        {
            "", "-c", ".local", new string('a', 129), " conda-forge", "conda forge",
            @"C:\channels", "file:///channels", "http://repo.example.org",
            "https://user:secret@repo.example.org/path", "https://@repo.example.org",
            "https://repo.example.org/path?", "https://repo.example.org/path#",
            "https://repo.example.org/t/mock-token", "https://repo.example.org/path/T/mock-token/",
            "https://repo.example.org/path/%74/mock-token/", "https://repo.example.org/path/t%2Fmock-token/",
            "https://repo.example.org/t//../mock-token", "https://repo.example.org/t//%2e%2e/mock-token",
            "https://repo.example.org/a b", "https://repo.example.org/a%20b",
            @"https://repo.example.org\channel"
        })
        {
            Expect.Throws<ArgumentException>(() => ChannelPolicy.Normalize(value));
        }

        Expect.Throws<ArgumentException>(() => ChannelSnapshot.Custom([]));
    }

    [Check("Channel snapshots retain immutable ordered flags and deduplicate normalized entries")]
    public static void SnapshotsKeepOrderedArguments()
    {
        var channels = new List<string>
        {
            "conda-forge", "CONDA-FORGE", "HTTPS://REPO.EXAMPLE.ORG/Main/",
            "https://repo.example.org/main", "defaults"
        };
        var snapshot = ChannelSnapshot.Custom(channels);
        channels.Clear();
        Expect.True(snapshot.IsOverride, "Custom snapshots override configured channels.");
        Expect.True(snapshot.Channels.SequenceEqual(["conda-forge", "https://repo.example.org/Main", "defaults"]),
            "Deduplication must preserve the first entry and its position.");
        if (snapshot.Channels is IList<string> mutableChannels)
            Expect.Throws<NotSupportedException>(() => mutableChannels[0] = "other");

        var arguments = new List<string> { "search" };
        ChannelSnapshot.Inherit.AppendArguments(arguments);
        Expect.True(!ChannelSnapshot.Inherit.IsOverride && ChannelSnapshot.Inherit.Channels.Count == 0,
            "Inherited snapshots have no channel entries.");
        Expect.True(arguments.SequenceEqual(["search"]), "Inherited snapshots add no flags.");

        snapshot.AppendArguments(arguments);
        Expect.True(arguments.SequenceEqual(
            ["search", "-c", "conda-forge", "-c", "https://repo.example.org/Main", "-c", "defaults", "--override-channels"]),
            "Custom snapshots append each channel in priority order followed by the override flag.");
    }

    [Check("Channel profiles round-trip separately for normalized installation roots")]
    public static void ProfilesAreScopedToRoots()
    {
        WithSettingsFile(path =>
        {
            var store = new ChannelProfileStore(path);
            var root = Path.Combine(Path.GetDirectoryName(path)!, "Install One");
            var otherRoot = Path.Combine(Path.GetDirectoryName(path)!, "Install Two");
            var missing = store.Load(root);
            Expect.True(!missing.Snapshot.IsOverride && missing.Warning is null,
                "A missing settings file should quietly inherit Conda channels.");

            store.Save(root, ChannelSnapshot.Custom(["defaults", "conda-forge"]));
            new ChannelProfileStore(path).Save(otherRoot, ChannelSnapshot.Custom(["community"]));
            var reloaded = new ChannelProfileStore(path);
            var first = reloaded.Load(root.ToUpperInvariant() + Path.DirectorySeparatorChar);
            Expect.True(first.Snapshot.Channels.SequenceEqual(["defaults", "conda-forge"]),
                "A fresh store should find the normalized root and preserve its saved channel order.");
            Expect.Equal("community", reloaded.Load(otherRoot).Snapshot.Channels.Single());

            reloaded.Save(root, ChannelSnapshot.Inherit);
            Expect.True(!store.Load(root).Snapshot.IsOverride, "Saving inherited mode removes the root override.");
            Expect.Equal("community", store.Load(otherRoot).Snapshot.Channels.Single());
        });
    }

    [Check("Malformed channel profiles inherit settings and show a generic diagnostic")]
    public static void MalformedProfilesFallBack()
    {
        WithSettingsFile(path =>
        {
            var store = new ChannelProfileStore(path);
            var root = Path.Combine(Path.GetDirectoryName(path)!, "Conda");
            var sensitiveEntry = "https://repo.example.org/t/mock-secret/";
            foreach (var document in new[]
            {
                "{",
                "[]",
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string[]>
                    { [root] = [sensitiveEntry] })
            })
            {
                File.WriteAllText(path, document);
                var result = store.Load(root);
                Expect.True(!result.Snapshot.IsOverride, "Malformed stored values should use inherited mode.");
                Expect.True(result.Warning is { Length: > 0 } warning &&
                    !warning.Contains(sensitiveEntry) && !warning.Contains(path),
                    "The warning must avoid raw profile contents and file paths.");
            }

            store.Save(root, ChannelSnapshot.Custom(["conda-forge"]));
            Expect.Equal("conda-forge", store.Load(root).Snapshot.Channels.Single());
        });
    }

    [Check("A malformed profile leaves other roots usable during load and repair")]
    public static void MalformedProfilePreservesOtherRoots()
    {
        WithSettingsFile(path =>
        {
            var store = new ChannelProfileStore(path);
            var first = Path.Combine(Path.GetDirectoryName(path)!, "Install One");
            var second = Path.Combine(Path.GetDirectoryName(path)!, "Install Two");
            var map = new Dictionary<string, object>
            {
                [first] = new[] { "defaults", "conda-forge" },
                [second] = new { channel = "malformed" }
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(map));

            var healthy = store.Load(first);
            Expect.True(healthy.Snapshot.Channels.SequenceEqual(["defaults", "conda-forge"]),
                "A malformed sibling profile must leave the valid profile available.");
            Expect.True(healthy.Warning is { Length: > 0 }, "Malformed entries should show a warning.");
            var malformed = store.Load(second);
            Expect.True(!malformed.Snapshot.IsOverride && malformed.Warning is { Length: > 0 },
                "The malformed root should fall back to inherited mode.");

            store.Save(second, ChannelSnapshot.Custom(["community"]));
            var repaired = store.Load(first);
            Expect.True(repaired.Snapshot.Channels.SequenceEqual(["defaults", "conda-forge"]),
                "Repairing one root must preserve the other root's profile.");
            Expect.True(repaired.Warning is null, "Saving the repaired settings should clear the warning.");
            Expect.Equal("community", store.Load(second).Snapshot.Channels.Single());
        });
    }

    [Check("A failed atomic channel profile write preserves the previous profile")]
    public static void FailedSavePreservesSettings()
    {
        if (!OperatingSystem.IsWindows())
            return;

        WithSettingsFile(path =>
        {
            var store = new ChannelProfileStore(path);
            var root = Path.Combine(Path.GetDirectoryName(path)!, "Conda");
            store.Save(root, ChannelSnapshot.Custom(["defaults"]));
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Expect.Throws<Exception>(
                    () => store.Save(root, ChannelSnapshot.Custom(["conda-forge"])));
                Expect.True(error is IOException or UnauthorizedAccessException,
                    "A blocked replacement must surface as a file system error.");
                Expect.Equal("defaults", store.Load(root).Snapshot.Channels.Single());
            }

            Expect.True(Directory.GetFiles(Path.GetDirectoryName(path)!).SequenceEqual([path]),
                "A failed replacement must clean up its temporary file.");
            store.Save(root, ChannelSnapshot.Custom(["conda-forge"]));
            Expect.Equal("conda-forge", store.Load(root).Snapshot.Channels.Single());
        });
    }

    private static void WithSettingsFile(Action<string> check)
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"channel-check-{Guid.NewGuid():N}"));
        var path = Path.Combine(directory.FullName, "channel-profiles.json");
        try
        {
            check(path);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }
}
