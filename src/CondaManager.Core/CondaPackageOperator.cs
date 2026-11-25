using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace CondaManager.Core;

public enum PackageAction
{
    Install,
    Update,
    Remove
}

public sealed record PackageOperationRequest(
    VerifiedInstallation Installation,
    string Prefix,
    PackageAction Action,
    string PackageName,
    string? Version,
    CondaPackage? InstalledPackage,
    IReadOnlyCollection<string> ProtectedRoots)
{
    public ChannelSnapshot Channels { get; init; } = ChannelSnapshot.Inherit;
    public bool ExactVersion { get; init; }
}

public sealed record PackageChange(string Name, string Version, string? Build);

public sealed record PackagePreview(
    PackageOperationRequest Request,
    IReadOnlyList<PackageChange> ToInstall,
    IReadOnlyList<PackageChange> ToRemove,
    IReadOnlyList<PackageChange> ToFetch)
{
    public bool HasChanges => ToInstall.Count > 0 || ToRemove.Count > 0 || ToFetch.Count > 0;
}

public interface ICondaPackageOperator
{
    Task<PackagePreview> PreviewAsync(PackageOperationRequest request, CancellationToken cancellationToken);
    Task ApplyAsync(PackageOperationRequest request, PackagePreview preview, CancellationToken cancellationToken);
}

public sealed class CondaPackageOperator(IProcessRunner runner) : ICondaPackageOperator
{
    private readonly ConditionalWeakTable<PackagePreview, object> _issuedPreviews = new();
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(45);
    private static readonly Regex ActionPackageNamePattern = new(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"\A[0-9][A-Za-z0-9._+!~-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExactVersionPattern = new(@"\A[A-Za-z0-9_][A-Za-z0-9._+!~-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ChangeVersionPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._+!~-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex BuildPattern = new(@"\A[A-Za-z0-9_+.*~-]{0,128}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ErrorClassPattern = new(@"\A[A-Za-z][A-Za-z0-9_]*(?:Error|Exception)\b",
        RegexOptions.CultureInvariant);

    public static string BuildInstallSpec(string name, string? version, bool exactVersion = false)
    {
        if (string.IsNullOrWhiteSpace(name) || !ActionPackageNamePattern.IsMatch(name))
            throw new ArgumentException("软件包名称须以字母、数字或下划线开头，只包含字母、数字、下划线、点或连字符，且不超过 128 个字符。", nameof(name));

        if (version is null or "")
        {
            if (exactVersion)
                throw new ArgumentException("精确安装必须指定版本。", nameof(version));
            return name;
        }

        if (exactVersion)
        {
            if (!ExactVersionPattern.IsMatch(version))
                throw new ArgumentException("所选软件包版本包含不支持的字符。", nameof(version));
            return $"{name}=={version}";
        }
        if (!VersionPattern.IsMatch(version))
            throw new ArgumentException("版本应为简单的 Conda 版本前缀，例如 1.26 或 3.12.2。", nameof(version));
        return $"{name}={version}";
    }

    public async Task<PackagePreview> PreviewAsync(PackageOperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = ValidateRequest(request);
        var arguments = BuildArguments(request, prefix, preview: true);

        var result = await runner.RunAsync(WithPrivateErrorReporting(request.Installation.Launcher),
            arguments, PreviewTimeout, cancellationToken).ConfigureAwait(false);
        EnsureExitCode(result, "预演");
        var preview = ParsePreview(result.Stdout, request, prefix);
        if (preview.HasChanges)
            _issuedPreviews.Add(preview, new object());
        return preview;
    }

    public async Task ApplyAsync(PackageOperationRequest request, PackagePreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);
        if (!ReferenceEquals(preview.Request, request) || !preview.HasChanges ||
            !_issuedPreviews.TryGetValue(preview, out _))
            throw new CondaOperationException("没有与当前请求匹配的有效预演结果，已阻止执行。");
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = ValidateRequest(request);
        var arguments = BuildArguments(request, prefix, preview: false);

        // Validation immediately precedes the mutating process call.
        ValidateRequest(request);
        if (!_issuedPreviews.Remove(preview))
            throw new CondaOperationException("预演结果已使用，请重新预览后再执行。");
        var result = await runner.RunAsync(WithPrivateErrorReporting(request.Installation.Launcher),
            arguments, ApplyTimeout, cancellationToken).ConfigureAwait(false);
        EnsureExitCode(result, "执行");
        EnsureSuccessJson(result.Stdout, prefix, "执行");
    }

    private static string ValidateRequest(PackageOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Installation);
        ArgumentNullException.ThrowIfNull(request.ProtectedRoots);
        ArgumentNullException.ThrowIfNull(request.Channels);
        if (request.Action is not (PackageAction.Install or PackageAction.Update or PackageAction.Remove))
            throw new CondaOperationException("不支持的软件包操作。");
        if (request.ExactVersion && request.Action != PackageAction.Install)
            throw new CondaOperationException("只有安装操作可以指定精确版本。");

        try
        {
            BuildPackageSpec(request);
        }
        catch (ArgumentException exception)
        {
            throw new CondaOperationException(exception.Message, exception);
        }

        if (request.Action == PackageAction.Install)
        {
            if (request.InstalledPackage is not null)
                throw new CondaOperationException("安装请求不能包含已选软件包。");
        }
        else
        {
            if (request.Version is not null ||
                request.InstalledPackage is null ||
                !string.Equals(request.InstalledPackage.Name, request.PackageName, StringComparison.OrdinalIgnoreCase) ||
                request.InstalledPackage.Origin != CondaPackageOrigin.Conda)
                throw new CondaOperationException("只能更新或卸载来源明确的 Conda 软件包。");
        }

        var target = NormalizePrefix(request.Prefix);
        if (!Directory.Exists(target) || !File.Exists(Path.Combine(target, "conda-meta", "history")))
            throw new CondaOperationException($"目标不是现存的 Conda 环境：{target}");

        EnsureNoReparsePoint(target);
        try
        {
            foreach (var root in new[] { request.Installation.Info.RootPrefix }.Concat(request.ProtectedRoots))
            {
                var protectedPath = NormalizePrefix(root);
                if (WindowsDirectoryIdentity.IsSameOrAncestor(target, protectedPath))
                    throw new CondaOperationException($"不能修改 Conda 安装根目录或其父目录中的软件包：{target}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or PlatformNotSupportedException)
        {
            throw new CondaOperationException($"无法确认环境与 Conda 安装目录的身份，已阻止操作：{target}", exception);
        }

        return target;
    }

    private static IReadOnlyList<string> BuildArguments(PackageOperationRequest request, string prefix,
        bool preview)
    {
        var verb = request.Action switch
        {
            PackageAction.Install => "install",
            PackageAction.Update => "update",
            PackageAction.Remove => "remove",
            _ => throw new CondaOperationException("不支持的软件包操作。")
        };
        var spec = BuildPackageSpec(request);
        var arguments = new List<string>
        {
            verb, "--prefix", prefix, spec, preview ? "--dry-run" : "--yes", "--json"
        };
        request.Channels.AppendArguments(arguments);
        return arguments;
    }

    private static string BuildPackageSpec(PackageOperationRequest request)
    {
        if (request.Action == PackageAction.Install)
            return BuildInstallSpec(request.PackageName, request.Version, request.ExactVersion);
        if (string.IsNullOrWhiteSpace(request.PackageName) ||
            !ActionPackageNamePattern.IsMatch(request.PackageName))
            throw new ArgumentException("已安装的软件包名称无效。", nameof(request.PackageName));
        return request.PackageName;
    }

    private static string NormalizePrefix(string prefix)
    {
        if (WindowsPathSafety.IsUnsupportedAlias(prefix))
            throw new CondaOperationException("不能对 Windows 路径别名执行软件包操作。");
        if (string.IsNullOrWhiteSpace(prefix) || !Path.IsPathFullyQualified(prefix))
            throw new CondaOperationException("环境路径必须是绝对路径。");
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix));
            if (WindowsPathSafety.IsUnsupportedAlias(normalized))
                throw new CondaOperationException("不能对 Windows 路径别名执行软件包操作。");
            return normalized;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CondaOperationException("环境路径无效。", exception);
        }
    }

    private static void EnsureNoReparsePoint(string target)
    {
        try
        {
            for (DirectoryInfo? current = new(target); current is not null; current = current.Parent)
            {
                if (File.GetAttributes(current.FullName).HasFlag(FileAttributes.ReparsePoint))
                    throw new CondaOperationException($"环境路径包含链接或重解析点，不能安全操作：{target}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CondaOperationException($"无法检查环境路径中的链接：{target}", exception);
        }
    }

    private static CondaLauncher WithPrivateErrorReporting(CondaLauncher launcher)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in launcher.EnvironmentOverrides)
            environment[name] = value;
        environment["CONDA_REPORT_ERRORS"] = "false";
        return launcher with { EnvironmentOverrides = environment };
    }

    private static void EnsureExitCode(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
            return;
        var detail = TryReadError(result.Stdout);
        throw new CondaOperationException($"Conda 软件包{operation}失败（退出码 {result.ExitCode}）" +
            (detail is null ? "。" : $"：{detail}"));
    }

    private static PackagePreview ParsePreview(string json, PackageOperationRequest request, string prefix)
    {
        using var document = ParseSuccessDocument(json, prefix, "预演");
        var root = document.RootElement;
        if (root.TryGetProperty("dry_run", out var dryRun) && dryRun.ValueKind != JsonValueKind.True)
            throw new CondaOperationException("Conda 未确认这是预演结果，已阻止执行。");

        if (!root.TryGetProperty("actions", out var actions))
        {
            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                string.Equals(message.GetString(), "All requested packages already installed.",
                    StringComparison.Ordinal))
                return new PackagePreview(request, [], [], []);
            throw new CondaOperationException("Conda 预演结果缺少可验证的变更信息。");
        }
        if (actions.ValueKind != JsonValueKind.Object)
            throw new CondaOperationException("Conda 预演的变更信息无效。");

        var hasGroup = false;
        foreach (var entry in actions.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "PREFIX":
                    break;
                case "FETCH":
                case "LINK":
                case "UNLINK":
                    hasGroup = true;
                    break;
                default:
                    throw new CondaOperationException("Conda 预演包含无法识别的变更类型，已阻止执行。");
            }
        }
        if (!hasGroup)
            throw new CondaOperationException("Conda 预演结果缺少可验证的变更信息。");

        return new PackagePreview(request,
            ParseActionGroup(actions, "LINK"),
            ParseActionGroup(actions, "UNLINK"),
            ParseActionGroup(actions, "FETCH"));
    }

    private static IReadOnlyList<PackageChange> ParseActionGroup(JsonElement actions, string name)
    {
        if (!actions.TryGetProperty(name, out var group))
            return [];
        if (group.ValueKind != JsonValueKind.Array)
            throw new CondaOperationException($"Conda 预演的 {name} 记录无效。");

        var changes = new List<PackageChange>();
        foreach (var item in group.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var packageName) ||
                packageName.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String)
                throw new CondaOperationException($"Conda 预演的 {name} 软件包记录无效。");

            var packageText = packageName.GetString()!;
            var versionText = version.GetString()!;
            if (!ActionPackageNamePattern.IsMatch(packageText) || !ChangeVersionPattern.IsMatch(versionText))
                throw new CondaOperationException($"Conda 预演的 {name} 软件包名称或版本无效。");
            string? build = null;
            if (item.TryGetProperty("build_string", out var buildString))
            {
                if (buildString.ValueKind != JsonValueKind.String)
                    throw new CondaOperationException($"Conda 预演的 {name} 软件包构建信息无效。");
                build = buildString.GetString();
            }
            else if (item.TryGetProperty("build", out var buildValue))
            {
                if (buildValue.ValueKind != JsonValueKind.String)
                    throw new CondaOperationException($"Conda 预演的 {name} 软件包构建信息无效。");
                build = buildValue.GetString();
            }
            if (build is not null && !BuildPattern.IsMatch(build))
                throw new CondaOperationException($"Conda 预演的 {name} 软件包构建信息无效。");
            changes.Add(new PackageChange(packageText, versionText, build));
        }
        return changes.AsReadOnly();
    }

    private static void EnsureSuccessJson(string json, string prefix, string operation)
    {
        using var _ = ParseSuccessDocument(json, prefix, operation);
    }

    private static JsonDocument ParseSuccessDocument(string json, string prefix, string operation)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new CondaOperationException($"Conda 软件包{operation}未返回有效的 JSON。", exception);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
        {
            document.Dispose();
            throw new CondaOperationException($"Conda 软件包{operation}未报告成功" +
                (TryReadError(json) is { } error ? $"：{error}" : "。"));
        }
        if (root.TryGetProperty("prefix", out var reportedPrefix) &&
            (reportedPrefix.ValueKind != JsonValueKind.String ||
             !SamePath(reportedPrefix.GetString()!, prefix)))
        {
            document.Dispose();
            throw new CondaOperationException($"Conda 软件包{operation}结果路径与目标不一致：{prefix}");
        }
        if (root.TryGetProperty("actions", out var actions) &&
            actions.ValueKind == JsonValueKind.Object &&
            actions.TryGetProperty("PREFIX", out var actionPrefix) &&
            (actionPrefix.ValueKind != JsonValueKind.String ||
             !SamePath(actionPrefix.GetString()!, prefix)))
        {
            document.Dispose();
            throw new CondaOperationException($"Conda 软件包{operation}变更的目标路径不一致：{prefix}");
        }
        return document;
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryReadError(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.String)
                return null;
            var firstLine = error.GetString()?.Split(['\r', '\n'])[0].Trim();
            if (string.IsNullOrWhiteSpace(firstLine))
                return null;
            var errorClass = ErrorClassPattern.Match(firstLine);
            return errorClass.Success ? errorClass.Value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
