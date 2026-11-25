using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CondaManager.Core;

public sealed record AvailablePackage(string Name, string Version, string Build, string ChannelLabel, string Subdir);

public sealed record PackageSearchPage(IReadOnlyList<AvailablePackage> Items, bool Truncated);

public interface ICondaPackageSearch
{
    Task<PackageSearchPage> SearchAsync(VerifiedInstallation installation, string query, ChannelSnapshot channels,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetConfiguredChannelsAsync(VerifiedInstallation installation,
        CancellationToken cancellationToken);
}

public sealed partial class CondaPackageSearch(IProcessRunner runner) : ICondaPackageSearch
{
    private const int MaxOutputBytes = 8 * 1024 * 1024;
    private const int MaxRows = 300;
    private const string SearchError = "Conda 软件包搜索失败。";
    private const string ConfigError = "无法读取 Conda 当前渠道。";
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(15);

    public static bool IsValidQuery(string? query) => query is not null && QueryPattern().IsMatch(query);

    public async Task<PackageSearchPage> SearchAsync(VerifiedInstallation installation, string query,
        ChannelSnapshot channels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(channels);
        if (!IsValidQuery(query))
            throw new ArgumentException("请输入 3–64 个字符，以英文字母或数字开头，仅使用英文字母、数字、点、下划线或连字符。", nameof(query));

        var arguments = new List<string> { "search", $"*{query}*", "--skip-flexible-search", "--json" };
        channels.AppendArguments(arguments);
        var result = await RunAsync(installation, arguments, SearchTimeout, SearchError, cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseObject(result.Stdout, SearchError);
        var root = document.RootElement;

        if (result.ExitCode != 0)
        {
            if (ReadString(root, "exception_name") is "PackagesNotFoundError" or "PackagesNotFoundInChannelsError")
                return new PackageSearchPage([], false);
            throw new CondaOperationException(SearchError);
        }

        var items = new List<AvailablePackage>();
        var truncated = false;
        foreach (var group in root.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Value.ValueKind != JsonValueKind.Array)
                throw new CondaOperationException(SearchError);

            foreach (var record in group.Value.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ReadString(record, "name");
                var version = ReadString(record, "version");
                var build = ReadString(record, "build");
                var subdir = ReadString(record, "subdir");
                if (name is null || !NamePattern().IsMatch(name) ||
                    version is null || !VersionPattern().IsMatch(version) ||
                    build is null || !BuildPattern().IsMatch(build) ||
                    subdir is null || !SimpleNamePattern().IsMatch(subdir))
                    throw new CondaOperationException(SearchError);

                if (items.Count < MaxRows)
                    items.Add(new AvailablePackage(name, version, build, ChannelLabel(ReadString(record, "channel")), subdir));
                else
                    truncated = true;
            }
        }
        return new PackageSearchPage(items.AsReadOnly(), truncated);
    }

    public async Task<IReadOnlyList<string>> GetConfiguredChannelsAsync(VerifiedInstallation installation,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(installation, ["config", "--show", "channels", "--json"],
            ConfigTimeout, ConfigError, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new CondaOperationException(ConfigError);

        using var document = ParseObject(result.Stdout, ConfigError);
        if (!document.RootElement.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
            throw new CondaOperationException(ConfigError);

        var labels = new List<string>();
        foreach (var channel in channels.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            labels.Add(ChannelLabel(ReadString(channel)));
        }
        return labels.AsReadOnly();
    }

    private async Task<ProcessResult> RunAsync(VerifiedInstallation installation, IReadOnlyList<string> arguments,
        TimeSpan timeout, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        var launcher = installation.Launcher;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in launcher.EnvironmentOverrides)
            environment[name] = value;
        environment["CONDA_REPORT_ERRORS"] = "false";

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(launcher with
            {
                EnvironmentOverrides = environment,
                MaxStdoutBytes = MaxOutputBytes,
                MaxStderrBytes = 1024 * 1024
            }, arguments,
                timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or
            Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CondaOperationException(error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static JsonDocument ParseObject(string stdout, string error)
    {
        if (stdout.Length > MaxOutputBytes || Encoding.UTF8.GetByteCount(stdout) > MaxOutputBytes)
            throw new CondaOperationException($"{error} Conda 输出超过 8 MiB。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            throw new CondaOperationException(error);
        }
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return document;
        document.Dispose();
        throw new CondaOperationException(error);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? ReadString(value)
            : null;

    private static string? ReadString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string ChannelLabel(string? value)
    {
        if (value is not null)
        {
            if (SimpleNamePattern().IsMatch(value))
                return value;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            {
                try
                {
                    var host = uri.IdnHost;
                    if (HostPattern().IsMatch(host))
                        return host;
                }
                catch (UriFormatException)
                {
                    // An invalid host has no display label.
                }
            }
        }
        return "来源未知";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{2,63}\z")]
    private static partial Regex QueryPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex SimpleNamePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._+!~-]{0,127}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_.+*~-]{1,128}\z")]
    private static partial Regex BuildPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9.:\[\]-]{1,253}\z")]
    private static partial Regex HostPattern();
}
