using System.Text.Json;

namespace CondaManager.Core;

public interface ICondaEnvironmentOperator
{
    Task CreateAsync(VerifiedInstallation installation, EnvironmentCreationPlan plan,
        CancellationToken cancellationToken);

    Task DeleteAsync(VerifiedInstallation installation, string prefix,
        IReadOnlyCollection<string> protectedRoots, string? activePrefix,
        CancellationToken cancellationToken);
}

public sealed class CondaOperationException : Exception
{
    public CondaOperationException(string message) : base(message)
    {
    }

    public CondaOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CondaEnvironmentOperator(IProcessRunner runner) : ICondaEnvironmentOperator
{
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromMinutes(20);

    public async Task CreateAsync(VerifiedInstallation installation, EnvironmentCreationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        EnvironmentCreationPlan checkedPlan;
        try
        {
            checkedPlan = EnvironmentCreationPlanner.Build(plan.Name, plan.ParentDirectory,
                plan.PythonVersion);
        }
        catch (ArgumentException exception)
        {
            throw new CondaOperationException(exception.Message, exception);
        }
        if (!SamePath(checkedPlan.Prefix, plan.Prefix))
            throw new CondaOperationException("创建目标与预览路径不一致。");

        EnsureTargetDoesNotExist(checkedPlan.Prefix);
        var pythonSpec = checkedPlan.PythonVersion is null
            ? "python"
            : $"python={checkedPlan.PythonVersion}";
        var arguments = new[]
        {
            "create", "--prefix", checkedPlan.Prefix, pythonSpec, "--yes", "--json"
        };

        // Conda's --yes can replace an existing environment. Check again at launch time.
        EnsureTargetDoesNotExist(checkedPlan.Prefix);
        var result = await runner.RunAsync(WithPrivateErrorReporting(installation.Launcher),
            arguments, CreateTimeout, cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(result, "创建", checkedPlan.Prefix, requireSuccessJson: true);

        if (!File.Exists(Path.Combine(checkedPlan.Prefix, "conda-meta", "history")))
            throw new CondaOperationException(
                $"Conda 报告创建成功，但目标缺少环境标记，请检查：{checkedPlan.Prefix}");
    }

    public async Task DeleteAsync(VerifiedInstallation installation, string prefix,
        IReadOnlyCollection<string> protectedRoots, string? activePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        cancellationToken.ThrowIfCancellationRequested();

        var target = NormalizeAbsolutePrefix(prefix);
        ValidateDeleteTarget(installation, target, protectedRoots, activePrefix);
        var arguments = new[]
        {
            "env", "remove", "--prefix", target, "--yes", "--json"
        };

        // Revalidate immediately before invoking the destructive command.
        ValidateDeleteTarget(installation, target, protectedRoots, activePrefix);
        var result = await runner.RunAsync(WithPrivateErrorReporting(installation.Launcher),
            arguments, DeleteTimeout, cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(result, "删除", target, requireSuccessJson: false);

        if (Directory.Exists(target) || File.Exists(target))
            throw new CondaOperationException($"Conda 已结束，但目标目录仍存在，请检查：{target}");
    }

    private static CondaLauncher WithPrivateErrorReporting(CondaLauncher launcher)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in launcher.EnvironmentOverrides)
            environment[name] = value;
        environment["CONDA_REPORT_ERRORS"] = "false";
        return launcher with { EnvironmentOverrides = environment };
    }

    private static void EnsureTargetDoesNotExist(string prefix)
    {
        try
        {
            if (Directory.Exists(prefix) || File.Exists(prefix))
                throw new CondaOperationException($"创建目标已存在，未执行 Conda：{prefix}");

            // GetAttributes also catches a reparse point whose target is unavailable.
            File.GetAttributes(prefix);
            throw new CondaOperationException($"创建目标已存在，未执行 Conda：{prefix}");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new CondaOperationException($"无法确认创建目标是否存在：{prefix}", exception);
        }
    }

    private static void ValidateDeleteTarget(VerifiedInstallation installation, string target,
        IReadOnlyCollection<string> protectedRoots, string? activePrefix)
    {
        var pathRoot = Path.GetPathRoot(target);
        if (pathRoot is null || SamePath(target, pathRoot))
            throw new CondaOperationException($"不能删除磁盘或共享根目录：{target}");

        var protectedPaths = new[] { installation.Info.RootPrefix }.Concat(protectedRoots)
            .Select(NormalizeAbsolutePrefix).ToArray();
        foreach (var protectedPath in protectedPaths)
        {
            if (IsSameOrAncestor(target, protectedPath))
                throw new CondaOperationException($"目标包含受保护的 Conda 安装根目录，不能删除：{target}");
        }

        string? normalizedActive = null;
        if (!string.IsNullOrWhiteSpace(activePrefix) && Path.IsPathFullyQualified(activePrefix))
        {
            normalizedActive = NormalizeAbsolutePrefix(activePrefix);
            if (IsSameOrAncestor(target, normalizedActive))
                throw new CondaOperationException($"不能删除当前激活的环境或其父目录：{target}");
        }

        if (!Directory.Exists(target) ||
            !File.Exists(Path.Combine(target, "conda-meta", "history")))
            throw new CondaOperationException($"目标不是现存的 Conda 环境，不能删除：{target}");

        EnsureNoReparsePoint(target);

        try
        {
            foreach (var protectedPath in protectedPaths)
            {
                if (WindowsDirectoryIdentity.IsSameOrAncestor(target, protectedPath))
                    throw new CondaOperationException($"目标包含受保护的 Conda 安装根目录，不能删除：{target}");
            }

            if (normalizedActive is not null &&
                WindowsDirectoryIdentity.IsSameOrAncestor(target, normalizedActive))
                throw new CondaOperationException($"不能删除当前激活的环境或其父目录：{target}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or PlatformNotSupportedException)
        {
            throw new CondaOperationException($"无法确认目标与受保护目录的身份，已阻止删除：{target}", exception);
        }
    }

    private static void EnsureNoReparsePoint(string target)
    {
        try
        {
            for (DirectoryInfo? current = new(target); current is not null; current = current.Parent)
            {
                if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new CondaOperationException($"目标路径包含链接或重解析点，不能安全删除：{target}");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new CondaOperationException($"无法检查目标路径中的链接：{target}", exception);
        }
    }

    private static string NormalizeAbsolutePrefix(string prefix)
    {
        if (WindowsPathSafety.IsUnsupportedAlias(prefix))
            throw new CondaOperationException("不能对 Windows 路径别名执行环境操作。");
        if (string.IsNullOrWhiteSpace(prefix) || !Path.IsPathFullyQualified(prefix))
            throw new CondaOperationException("环境路径必须是绝对路径。");
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix));
            if (WindowsPathSafety.IsUnsupportedAlias(normalized))
                throw new CondaOperationException("不能对 Windows 路径别名执行环境操作。");
            return normalized;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CondaOperationException("环境路径无效。", exception);
        }
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

    private static bool IsSameOrAncestor(string candidate, string protectedPath)
    {
        if (SamePath(candidate, protectedPath))
            return true;
        var prefix = Path.EndsInDirectorySeparator(candidate)
            ? candidate
            : candidate + Path.DirectorySeparatorChar;
        return protectedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureCommandSucceeded(ProcessResult result, string operation,
        string expectedPrefix, bool requireSuccessJson)
    {
        if (result.ExitCode != 0)
        {
            var detail = TryReadError(result.Stdout);
            var suffix = detail is null ? "" : $"：{detail}";
            throw new CondaOperationException(
                $"Conda {operation}失败（退出码 {result.ExitCode}）{suffix}");
        }

        if (string.IsNullOrWhiteSpace(result.Stdout))
        {
            if (requireSuccessJson)
                throw new CondaOperationException($"Conda {operation}未返回可验证的 JSON 结果：{expectedPrefix}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CondaOperationException($"Conda {operation}返回的 JSON 结果无效：{expectedPrefix}");

            if (root.TryGetProperty("success", out var success))
            {
                if (success.ValueKind != JsonValueKind.True)
                    throw new CondaOperationException(
                        $"Conda {operation}未报告成功：{TryReadError(result.Stdout) ?? expectedPrefix}");
            }
            else if (requireSuccessJson)
            {
                throw new CondaOperationException($"Conda {operation}未报告成功：{expectedPrefix}");
            }

            if (root.TryGetProperty("prefix", out var prefix) &&
                (prefix.ValueKind != JsonValueKind.String ||
                 !SamePath(prefix.GetString()!, expectedPrefix)))
                throw new CondaOperationException($"Conda {operation}结果路径与请求不一致：{expectedPrefix}");
        }
        catch (JsonException exception)
        {
            throw new CondaOperationException($"Conda {operation}返回的 JSON 结果无效：{expectedPrefix}", exception);
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
            return firstLine.Length <= 240 ? firstLine : firstLine[..240] + "…";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
