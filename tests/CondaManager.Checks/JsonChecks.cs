using CondaManager.Core;

namespace CondaManager.Checks;

public static class JsonChecks
{
    [Check("info parses required fields and normalizes Windows paths")]
    public static void InfoParsesRequiredFields()
    {
        const string json = """{"root_prefix":"C:\\Tools\\..\\Miniconda3\\","conda_version":"24.11.0","envs_dirs":["C:\\Miniconda3\\envs\\","D:\\研究 环境\\envs"],"python_version":"3.12"}""";

        var info = CondaJson.ParseInfo(json);

        Expect.Equal(@"C:\Miniconda3", info.RootPrefix);
        Expect.Equal("24.11.0", info.CondaVersion);
        Expect.Equal(2, info.EnvsDirs.Count);
        Expect.Equal(@"C:\Miniconda3\envs", info.EnvsDirs[0]);
        Expect.Equal(@"D:\研究 环境\envs", info.EnvsDirs[1]);
    }

    [Check("info accepts an empty envs_dirs array")]
    public static void InfoAcceptsEmptyEnvsDirs()
    {
        var info = CondaJson.ParseInfo("""{"root_prefix":"C:\\Conda","conda_version":"25.1","envs_dirs":[]}""");

        Expect.Equal(0, info.EnvsDirs.Count);
    }

    [Check("info rejects missing, null, and wrongly typed required fields")]
    public static void InfoRejectsInvalidRequiredFields()
    {
        AssertRejected(CondaJson.ParseInfo,
            """{"conda_version":"25.1","envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":"25.1"}""",
            """{"root_prefix":null,"conda_version":"25.1","envs_dirs":[]}""",
            """{"root_prefix":7,"conda_version":"25.1","envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":null,"envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":false,"envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":"","envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":"  ","envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":"25.1","envs_dirs":null}""",
            """{"root_prefix":"C:\\Conda","conda_version":"25.1","envs_dirs":"C:\\envs"}""",
            """{"root_prefix":"C:\\Conda","conda_version":"25.1","envs_dirs":[null]}""",
            """{"root_prefix":"C:\\Conda","conda_version":"25.1","envs_dirs":[12]}""");
    }

    [Check("info rejects relative and partially qualified paths")]
    public static void InfoRejectsRelativePaths()
    {
        AssertRejected(CondaJson.ParseInfo,
            """{"root_prefix":"Conda","conda_version":"25.1","envs_dirs":[]}""",
            """{"root_prefix":"C:Conda","conda_version":"25.1","envs_dirs":[]}""",
            """{"root_prefix":"\\Conda","conda_version":"25.1","envs_dirs":[]}""",
            """{"root_prefix":"C:\\Conda","conda_version":"25.1","envs_dirs":["envs"]}""");
    }

    [Check("env list parses normalized prefixes and accepts an empty array")]
    public static void EnvironmentPrefixesParse()
    {
        var prefixes = CondaJson.ParseEnvironmentPrefixes("""{"envs":["C:\\Conda\\envs\\alpha\\","D:\\研究 环境\\beta"],"extra":true}""");

        Expect.Equal(2, prefixes.Count);
        Expect.Equal(@"C:\Conda\envs\alpha", prefixes[0]);
        Expect.Equal(@"D:\研究 环境\beta", prefixes[1]);
        Expect.Equal(0, CondaJson.ParseEnvironmentPrefixes("""{"envs":[]}""").Count);
    }

    [Check("env list rejects invalid envs fields and paths")]
    public static void EnvironmentPrefixesRejectInvalidData()
    {
        AssertRejected(CondaJson.ParseEnvironmentPrefixes,
            """{}""",
            """{"envs":null}""",
            """{"envs":{}}""",
            """{"envs":[null]}""",
            """{"envs":[false]}""",
            """{"envs":["envs\\alpha"]}""",
            """{"envs":["C:envs\\alpha"]}""");
    }

    [Check("packages parse required and optional fields and ignore unknown fields")]
    public static void PackagesParse()
    {
        var packages = CondaJson.ParsePackages("""[{"name":"python","version":"3.12.7","build_string":"h123_0","channel":"conda-forge","extra":1},{"name":"numpy","version":"2.1.0"}]""");

        Expect.Equal(2, packages.Count);
        Expect.Equal("python", packages[0].Name);
        Expect.Equal("3.12.7", packages[0].Version);
        Expect.Equal("h123_0", packages[0].Build);
        Expect.Equal("conda-forge", packages[0].Channel);
        Expect.Equal("numpy", packages[1].Name);
        Expect.Equal("2.1.0", packages[1].Version);
        Expect.Equal<string?>(null, packages[1].Build);
        Expect.Equal<string?>(null, packages[1].Channel);
    }

    [Check("packages accept an empty array")]
    public static void PackagesAcceptEmptyArray()
    {
        Expect.Equal(0, CondaJson.ParsePackages("[]").Count);
    }

    [Check("packages reject invalid records and optional field types")]
    public static void PackagesRejectInvalidData()
    {
        AssertRejected(CondaJson.ParsePackages,
            """{}""",
            "null",
            """[null]""",
            """[3]""",
            """[{"version":"1"}]""",
            """[{"name":"python"}]""",
            """[{"name":null,"version":"1"}]""",
            """[{"name":false,"version":"1"}]""",
            """[{"name":" ","version":"1"}]""",
            """[{"name":"python","version":null}]""",
            """[{"name":"python","version":7}]""",
            """[{"name":"python","version":""}]""",
            """[{"name":"python","version":"1","build_string":2}]""",
            """[{"name":"python","version":"1","channel":{}}]""",
            """[{"name":"python","version":"1","channel":null}]""",
            """[{"name":"python","version":"1","package_type":2}]""");
    }

    [Check("all parsers report malformed JSON and wrong top-level shapes as data errors")]
    public static void ParsersRejectMalformedJsonAndTopLevelShape()
    {
        AssertRejected(CondaJson.ParseInfo, "{", "[]", "null");
        AssertRejected(CondaJson.ParseEnvironmentPrefixes, "{", "[]", "null");
        AssertRejected(CondaJson.ParsePackages, "[", "{}", "null");
    }

    private static void AssertRejected<T>(Func<string, T> parse, params string[] fixtures)
    {
        foreach (var fixture in fixtures)
            Expect.Throws<CondaDataException>(() => parse(fixture));
    }
}
