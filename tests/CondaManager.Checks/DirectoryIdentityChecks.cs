using CondaManager.Core;

namespace CondaManager.Checks;

public static class DirectoryIdentityChecks
{
    [Check("directory identity matches normal and Windows device aliases of one directory")]
    public static void AliasesShareIdentity()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var directory = tree.CreateDirectory("identity-target");
        var ordinary = WindowsDirectoryIdentity.Get(directory);
        var extended = WindowsDirectoryIdentity.Get(@"\\?\" + directory);
        var device = WindowsDirectoryIdentity.Get(@"\\.\" + directory);

        Expect.Equal(ordinary, extended);
        Expect.Equal(ordinary, device);
    }

    [Check("directory identity distinguishes separate directories")]
    public static void DistinctDirectoriesHaveDistinctIdentity()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var first = WindowsDirectoryIdentity.Get(tree.CreateDirectory("first-identity"));
        var second = WindowsDirectoryIdentity.Get(tree.CreateDirectory("second-identity"));

        Expect.True(first != second, "Different directories on the same volume need different file indexes.");
        Expect.True(first.FileIndex != 0 && second.FileIndex != 0,
            "A zero file index cannot establish directory identity safely.");
    }

    [Check("directory identity ancestry works across alternate Windows path spellings")]
    public static void AncestorIdentityAcrossAliases()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        var parent = tree.CreateDirectory("physical-parent");
        var child = tree.CreateDirectory("physical-parent", "child");

        Expect.True(WindowsDirectoryIdentity.IsSameOrAncestor(parent, @"\\?\" + child),
            "An alternate spelling must not hide a protected descendant.");
        Expect.True(WindowsDirectoryIdentity.IsSameOrAncestor(@"\\.\" + parent, child),
            "A device spelling of the candidate must identify the same parent.");
        Expect.True(!WindowsDirectoryIdentity.IsSameOrAncestor(child, parent),
            "A child is not the ancestor of its parent.");
    }

    [Check("directory identity fails closed for relative or missing paths")]
    public static void InvalidDirectoryCannotBeIdentified()
    {
        using var tree = new DiscoveryChecks.DiscoveryTree();
        Expect.Throws<ArgumentException>(() => WindowsDirectoryIdentity.Get("relative-directory"));
        Expect.Throws<IOException>(() => WindowsDirectoryIdentity.Get(Path.Combine(tree.Base, "missing")));
    }
}
