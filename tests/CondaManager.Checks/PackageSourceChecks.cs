using CondaManager.Core;

namespace CondaManager.Checks;

public static class PackageSourceChecks
{
    [Check("package list accepts null package_type as absent metadata")]
    public static void NullPackageTypeIsAbsent()
    {
        var packages = CondaJson.ParsePackages("""
            [{"name":"python","version":"3.12","channel":"conda-forge","package_type":null}]
            """);

        Expect.Equal<string?>(null, packages[0].PackageType);
        Expect.Equal(CondaPackageOrigin.Conda, packages[0].Origin);
    }

    [Check("Conda's unknown-channel placeholder leaves package origin unknown")]
    public static void UnknownChannelDoesNotIdentifyOrigin()
    {
        var packages = CondaJson.ParsePackages("""
            [{"name":"unknown","version":"1","channel":"<unknown>"}]
            """);

        Expect.Equal(CondaPackageOrigin.Unknown, packages[0].Origin);
    }

    [Check("package list identifies pip, Conda, and unknown origins from metadata")]
    public static void ClassifiesPackageOrigins()
    {
        var packages = CondaJson.ParsePackages("""
            [
                {"name":"python","version":"3.12","channel":"conda-forge"},
                {"name":"openssl","version":"3.5","package_type":"conda"},
                {"name":"requests","version":"2.32","channel":"PyPI","package_type":"conda"},
                {"name":"project","version":"1.0","channel":"<develop>"},
                {"name":"wheel","version":"1.0","channel":"defaults","package_type":"virtual_python_wheel"},
                {"name":"egg","version":"1.0","package_type":"virtual_python_egg_manageable"},
                {"name":"urllib3","version":"2.5","build_string":"pypi_0"},
                {"name":"unknown","version":"1","channel":" ","package_type":"other"},
                {"name":"no-metadata","version":"1"}
            ]
            """);

        Expect.Equal(CondaPackageOrigin.Conda, packages[0].Origin);
        Expect.Equal(CondaPackageOrigin.Conda, packages[1].Origin);
        Expect.Equal(CondaPackageOrigin.Pip, packages[2].Origin);
        Expect.Equal(CondaPackageOrigin.Pip, packages[3].Origin);
        Expect.Equal(CondaPackageOrigin.Pip, packages[4].Origin);
        Expect.Equal(CondaPackageOrigin.Pip, packages[5].Origin);
        Expect.Equal(CondaPackageOrigin.Pip, packages[6].Origin);
        Expect.Equal(CondaPackageOrigin.Unknown, packages[7].Origin);
        Expect.Equal(CondaPackageOrigin.Unknown, packages[8].Origin);
        Expect.Equal("virtual_python_wheel", packages[4].PackageType);
        Expect.Equal<string?>(null, packages[8].PackageType);
    }
}
