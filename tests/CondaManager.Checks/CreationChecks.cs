using CondaManager.Core;

namespace CondaManager.Checks;

public static class CreationChecks
{
    [Check("creation chooses the first writable configured environment directory")]
    public static void DefaultParentUsesFirstWritableCandidate()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var first = tree.CreateDirectory("read-only-candidate");
        var second = tree.CreateDirectory("writable-candidate");
        var later = tree.CreateDirectory("later-candidate");
        var probe = new RecordingProbe(path => path == second);
        var info = new CondaInfo(tree.Base, "25.5.1", [first, second + Path.DirectorySeparatorChar, later]);

        var selected = EnvironmentCreationPlanner.ChooseDefaultParent(info, probe);

        Expect.Equal(second, selected);
        Expect.Equal(2, probe.Calls.Count);
        Expect.Equal(first, probe.Calls[0]);
        Expect.Equal(second, probe.Calls[1]);
    }

    [Check("creation requires custom location when no configured directory is writable")]
    public static void NoWritableDefault()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var candidate = tree.CreateDirectory("candidate");
        var probe = new RecordingProbe(_ => false);
        var info = new CondaInfo(tree.Base, "25.5.1", ["relative", candidate]);

        var selected = EnvironmentCreationPlanner.ChooseDefaultParent(info, probe);

        Expect.True(selected is null, "No unwritable or relative candidate should be selected.");
        Expect.Equal(1, probe.Calls.Count);
    }

    [Check("writable directory probe does not create the intended destination")]
    public static void WritableProbeLeavesDestinationAbsent()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var existing = tree.CreateDirectory("existing-parent");
        var candidate = Path.Combine(existing, "not-created", "envs");

        Expect.True(new WritableDirectoryProbe().CanCreateChild(candidate),
            "An existing writable ancestor should support a new environment directory.");
        Expect.True(!Directory.Exists(candidate), "Probing must not create the target directory.");
        Expect.Equal(0, Directory.GetFileSystemEntries(existing).Length);
    }

    [Check("creation preview normalizes Unicode paths and optional Python version")]
    public static void BuildPreviewMatchesPrefix()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var parent = tree.CreateDirectory("自定义 目录");

        var plan = EnvironmentCreationPlanner.Build("分析环境_2", parent + Path.DirectorySeparatorChar,
            " 3.12.2 ");

        Expect.Equal("分析环境_2", plan.Name);
        Expect.Equal(parent, plan.ParentDirectory);
        Expect.Equal(Path.Combine(parent, "分析环境_2"), plan.Prefix);
        Expect.Equal("3.12.2", plan.PythonVersion);
        var unpinned = EnvironmentCreationPlanner.Build("work-2", parent, "  ");
        Expect.True(unpinned.PythonVersion is null, "A blank version should request the channel default Python.");
    }

    [Check("creation rejects invalid or reserved environment names")]
    public static void RejectsInvalidNames()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var parent = tree.CreateDirectory("parent");
        var invalidNames = new[]
        {
            "", " ", ".", "..", "a b", "a/b", "a\\b", "a#b", "a.b", "CON", "aux", "COM1", "LPT9",
            new string('x', 65)
        };

        foreach (var name in invalidNames)
            Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build(name, parent, null));
    }

    [Check("creation rejects relative parent paths and invalid Python versions")]
    public static void RejectsInvalidParentAndVersion()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var parent = tree.CreateDirectory("parent");
        var file = Path.Combine(parent, "not-a-directory");
        File.WriteAllText(file, "");

        Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build("valid", "relative-parent", null));
        Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build("valid", file, null));
        Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build("valid", @"\\?\" + parent, null));
        Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build("valid", @"\\.\" + parent, null));
        var shortNameStyle = tree.CreateDirectory("short~1");
        Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build("valid", shortNameStyle, null));
        foreach (var version in new[] { "3", "3.12rc1", "3.12;exit", "3.12.1.0" })
            Expect.Throws<ArgumentException>(() => EnvironmentCreationPlanner.Build("valid", parent, version));
    }

    private sealed class RecordingProbe(Func<string, bool> check) : IWritableDirectoryProbe
    {
        public List<string> Calls { get; } = [];

        public bool CanCreateChild(string directory)
        {
            Calls.Add(directory);
            return check(directory);
        }
    }
}
