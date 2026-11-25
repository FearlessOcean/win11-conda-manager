using System.Text.Json;
using CondaManager.Core;

namespace CondaManager.Checks;

public static class PackageSearchChecks
{
    private const string PrivateMarker = "PRIVATE_TOKEN";
    private static readonly VerifiedInstallation Installation = new(
        new CondaInfo(@"C:\conda", "25.5.1", []),
        new CondaLauncher(@"C:\conda\python.exe", ["-m", "conda"])
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["conda_report_errors"] = "true",
                ["OTHER"] = "preserve"
            }
        });

    [Check("available package search uses the verified launcher and a literal wildcard query")]
    public static async Task SearchArgumentsAndResults()
    {
        var runner = new FakeRunner(new ProcessResult(0, """
            {"numpy":[
              {"name":"numpy","version":"2.1.0","build":"py312_0","subdir":"win-64",
               "channel":"https://user:PRIVATE_TOKEN@Packages.Example/t/PRIVATE_TOKEN/channel?x=PRIVATE_TOKEN",
               "url":"https://hidden.example/PRIVATE_TOKEN"},
              {"name":"numpy","version":"2.0.0","build":"py311_0","subdir":"noarch","channel":"conda-forge"}
            ]}
            """, ""));
        var search = new CondaPackageSearch(runner);
        var page = await search.SearchAsync(Installation, "Num.py-2_a", ChannelSnapshot.Inherit, CancellationToken.None);

        Expect.True(runner.Arguments.SequenceEqual(["search", "*Num.py-2_a*", "--skip-flexible-search", "--json"]),
            "Inherited search must use one MatchSpec and keep the inherited channels.");
        Expect.Equal(Installation.Launcher.ExecutablePath, runner.Launcher!.ExecutablePath);
        Expect.True(runner.Launcher.LeadingArguments.SequenceEqual(["-m", "conda"]), "Keep the launcher prefix.");
        Expect.Equal("false", runner.Launcher.EnvironmentOverrides["CONDA_REPORT_ERRORS"]);
        Expect.Equal("preserve", runner.Launcher.EnvironmentOverrides["OTHER"]);
        Expect.Equal("true", Installation.Launcher.EnvironmentOverrides["conda_report_errors"]);
        Expect.Equal(8 * 1024 * 1024, runner.Launcher.MaxStdoutBytes);
        Expect.Equal(1024 * 1024, runner.Launcher.MaxStderrBytes);
        Expect.True(runner.Timeout > TimeSpan.Zero && runner.Timeout <= TimeSpan.FromMinutes(5), "Bound search duration.");
        Expect.Equal(new AvailablePackage("numpy", "2.1.0", "py312_0", "packages.example", "win-64"), page.Items[0]);
        Expect.Equal("conda-forge", page.Items[1].ChannelLabel);
        Expect.Equal(false, page.Truncated);
        Expect.Equal(2, page.Items.Count);
    }

    [Check("available package search appends ordered custom channel arguments")]
    public static async Task CustomChannels()
    {
        var runner = new FakeRunner(new ProcessResult(0, "{}", ""));
        var channels = ChannelSnapshot.Custom(["conda-forge", "https://packages.example/stable"]);
        await new CondaPackageSearch(runner).SearchAsync(Installation, "numpy", channels, CancellationToken.None);
        Expect.True(runner.Arguments.SequenceEqual([
            "search", "*numpy*", "--skip-flexible-search", "--json",
            "-c", "conda-forge", "-c", "https://packages.example/stable", "--override-channels"]),
            "Custom channels must follow the search options in priority order.");
    }

    [Check("invalid available package queries never reach the runner")]
    public static async Task RejectsInvalidQueries()
    {
        var runner = new FakeRunner(new ProcessResult(0, "{}", ""));
        var search = new CondaPackageSearch(runner);
        foreach (var query in new[] { "ab", new string('a', 65), "-abc", "_abc", ".abc", "a b", "abc\n", "中文包", "a*b", "a::b" })
            await Expect.ThrowsAsync<ArgumentException>(() =>
                search.SearchAsync(Installation, query, ChannelSnapshot.Inherit, CancellationToken.None));
        Expect.Equal(0, runner.Calls);
    }

    [Check("available package search limits results and rejects oversized stdout by byte count")]
    public static async Task OutputLimits()
    {
        var packages = Enumerable.Range(0, 301)
            .Select(index => new { name = "numpy", version = "1.0", build = $"build_{index}", subdir = "win-64" });
        var page = await SearchAsync(new ProcessResult(0, JsonSerializer.Serialize(new { numpy = packages }), ""));
        Expect.Equal(300, page.Items.Count);
        Expect.Equal("build_299", page.Items[^1].Build);
        Expect.Equal(true, page.Truncated);

        var oversized = "{\"ignored\":\"" + new string('界', 8 * 1024 * 1024 / 3) + "\"}";
        var error = await Expect.ThrowsAsync<CondaOperationException>(() => SearchAsync(new ProcessResult(0, oversized, "")));
        Expect.True(error.Message.Contains("8 MiB", StringComparison.Ordinal), "Oversized stdout must be detected before parsing.");
    }

    [Check("available package search maps only known no-match exceptions to empty results")]
    public static async Task NoMatchAndGenericErrors()
    {
        foreach (var name in new[] { "PackagesNotFoundError", "PackagesNotFoundInChannelsError" })
        {
            var errorJson = JsonSerializer.Serialize(new { exception_name = name, message = PrivateMarker });
            var page = await SearchAsync(new ProcessResult(1, errorJson, PrivateMarker));
            Expect.Equal(0, page.Items.Count);
            Expect.Equal(false, page.Truncated);
        }

        var error = await Expect.ThrowsAsync<CondaOperationException>(() => SearchAsync(new ProcessResult(
            1, """{"exception_name":"UnavailableInvalidChannel","message":"https://example.test/t/PRIVATE_TOKEN"}""",
            PrivateMarker)));
        AssertPrivate(error);
    }

    [Check("available package search rejects malformed JSON and unsafe package fields")]
    public static async Task MalformedOutput()
    {
        foreach (var stdout in new[]
        {
            "", "{", "[]", "{\"numpy\":{}}", "{\"numpy\":[42]}", "{\"numpy\":[{}]}",
            """
            {"numpy":[{"name":"numpy","version":"1.*","build":"py312_0","subdir":"win-64"}]}
            """,
            """
            {"numpy":[{"name":"numpy","version":"-1.0","build":"py312_0","subdir":"win-64"}]}
            """,
            """
            {"numpy":[{"name":"numpy","version":"2.0","build":"https://example.test/t/PRIVATE_TOKEN","subdir":"win-64"}]}
            """
        })
        {
            var error = await Expect.ThrowsAsync<CondaOperationException>(() =>
                SearchAsync(new ProcessResult(0, stdout, PrivateMarker)));
            AssertPrivate(error);
        }
    }

    [Check("configured channel lookup uses read-only config and sanitizes labels")]
    public static async Task ConfiguredChannels()
    {
        var runner = new FakeRunner(new ProcessResult(0, """
            {"channels":["defaults","https://packages.example/t/PRIVATE_TOKEN/stable",
             "file:///PRIVATE_TOKEN","bad/PRIVATE_TOKEN",42]}
            """, ""));
        var channels = await new CondaPackageSearch(runner).GetConfiguredChannelsAsync(Installation, CancellationToken.None);
        Expect.True(runner.Arguments.SequenceEqual(["config", "--show", "channels", "--json"]),
            "Config lookup must use only the read-only command.");
        Expect.True(channels.SequenceEqual(["defaults", "packages.example", "来源未知", "来源未知", "来源未知"]),
            "Configured labels must follow the search result display policy.");
    }

    [Check("available package search observes cancellation and sanitizes runner failures")]
    public static async Task CancellationAndFailures()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(new ProcessResult(0, "{}", ""));
        var search = new CondaPackageSearch(runner);
        runner.Respond = token =>
        {
            Expect.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult(new ProcessResult(0, "{}", ""));
        };
        var error = await Expect.ThrowsAsync<OperationCanceledException>(() =>
            search.SearchAsync(Installation, "numpy", ChannelSnapshot.Inherit, cancellation.Token));
        Expect.Equal(cancellation.Token, error.CancellationToken);
        await Expect.ThrowsAsync<OperationCanceledException>(() =>
            search.SearchAsync(Installation, "numpy", ChannelSnapshot.Inherit, cancellation.Token));
        Expect.Equal(1, runner.Calls);

        runner.Respond = _ => throw new TimeoutException(PrivateMarker);
        AssertPrivate(await Expect.ThrowsAsync<CondaOperationException>(() =>
            search.SearchAsync(Installation, "numpy", ChannelSnapshot.Inherit, CancellationToken.None)));
    }

    private static Task<PackageSearchPage> SearchAsync(ProcessResult result) =>
        new CondaPackageSearch(new FakeRunner(result)).SearchAsync(
            Installation, "numpy", ChannelSnapshot.Inherit, CancellationToken.None);

    private static void AssertPrivate(Exception error) =>
        Expect.True(!error.ToString().Contains(PrivateMarker, StringComparison.Ordinal), "Do not expose raw process details.");

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public CondaLauncher? Launcher { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public Func<CancellationToken, Task<ProcessResult>> Respond { get; set; } = _ => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            Launcher = launcher;
            Arguments = arguments;
            Timeout = timeout;
            return Respond(cancellationToken);
        }
    }
}
