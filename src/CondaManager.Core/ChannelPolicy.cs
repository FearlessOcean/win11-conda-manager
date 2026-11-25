using System.Text.RegularExpressions;

namespace CondaManager.Core;

public static partial class ChannelPolicy
{
    public static string Normalize(string channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (SimpleChannelName().IsMatch(channel))
            return channel;

        const string httpsPrefix = "https://";
        if (!channel.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase) ||
            channel.Any(char.IsWhiteSpace) || channel.Contains('\\') ||
            channel.Contains('?') || channel.Contains('#') ||
            !Uri.TryCreate(channel, UriKind.Absolute, out var uri) ||
            !uri.IsWellFormedOriginalString() || string.IsNullOrEmpty(uri.Host))
        {
            throw InvalidChannel();
        }

        // Keep the original path spelling and port because channel paths may be case sensitive.
        var pathStart = channel.IndexOf('/', httpsPrefix.Length);
        var authority = pathStart < 0 ? channel[httpsPrefix.Length..] : channel[httpsPrefix.Length..pathStart];
        var path = pathStart < 0 ? string.Empty : channel[pathStart..];
        var decodedPath = Uri.UnescapeDataString(path);
        if (authority.Contains('@') || decodedPath.Any(char.IsWhiteSpace) ||
            decodedPath.Any(char.IsControl) || decodedPath.Contains('\\') ||
            TokenPath().IsMatch(decodedPath) || TokenPath().IsMatch(Uri.UnescapeDataString(uri.AbsolutePath)))
        {
            throw InvalidChannel();
        }

        return httpsPrefix + authority.ToLowerInvariant() + path.TrimEnd('/');
    }

    private static ArgumentException InvalidChannel() =>
        new("请输入有效的 Conda 渠道名称或不含凭据、查询参数和片段的 HTTPS 地址。", "channel");

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex SimpleChannelName();

    [GeneratedRegex("/t/[^/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPath();
}

public sealed class ChannelSnapshot
{
    private ChannelSnapshot(IReadOnlyList<string> channels)
    {
        Channels = channels;
    }

    public static ChannelSnapshot Inherit { get; } = new(Array.Empty<string>());

    public static ChannelSnapshot Custom(IEnumerable<string> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in channels)
        {
            var channel = ChannelPolicy.Normalize(entry);
            if (seen.Add(channel))
                ordered.Add(channel);
        }

        if (ordered.Count == 0)
            throw new ArgumentException("自定义模式至少需要一个渠道。", nameof(channels));

        return new ChannelSnapshot(ordered.AsReadOnly());
    }

    public bool IsOverride => Channels.Count > 0;
    public IReadOnlyList<string> Channels { get; }

    public void AppendArguments(List<string> args)
    {
        if (!IsOverride)
            return;

        foreach (var channel in Channels)
        {
            args.Add("-c");
            args.Add(channel);
        }

        args.Add("--override-channels");
    }
}
