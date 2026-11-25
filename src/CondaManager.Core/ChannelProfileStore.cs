using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CondaManager.Core;

public sealed record ChannelProfileLoadResult(ChannelSnapshot Snapshot, string? Warning);

public interface IChannelProfileStore
{
    ChannelProfileLoadResult Load(string root);
    void Save(string root, ChannelSnapshot snapshot);
}

public sealed class ChannelProfileStore : IChannelProfileStore
{
    private readonly string _filePath;
    private readonly string _mutexName;

    public ChannelProfileStore(string? filePath = null)
    {
        filePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CondaManager", "channel-profiles.json");
        if (!Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("渠道设置文件必须使用绝对路径。", nameof(filePath));

        _filePath = Path.GetFullPath(filePath);
        var pathBytes = Encoding.UTF8.GetBytes(_filePath.ToUpperInvariant());
        _mutexName = "CondaManager.ChannelProfiles." + Convert.ToHexString(SHA256.HashData(pathBytes));
    }

    public ChannelProfileLoadResult Load(string root)
    {
        try
        {
            var key = NormalizeRoot(root);
            var profiles = ReadProfiles(out var malformedEntries);
            var warning = malformedEntries ? "部分应用渠道设置无效，受影响的安装将沿用 Conda 当前渠道。" : null;
            return new(profiles.GetValueOrDefault(key, ChannelSnapshot.Inherit), warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          SecurityException or JsonException or ArgumentException or
                                          NotSupportedException)
        {
            return new(ChannelSnapshot.Inherit, "无法读取应用渠道设置，已沿用 Conda 当前渠道。");
        }
    }

    public void Save(string root, ChannelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var key = NormalizeRoot(root);
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                throw new IOException("渠道设置正在由另一个应用实例保存。");
        }
        catch (AbandonedMutexException)
        {
            // The previous writer exited. The published settings file is still atomic.
        }

        try
        {
            Dictionary<string, ChannelSnapshot> profiles;
            try
            {
                profiles = ReadProfiles(out _);
            }
            catch (JsonException)
            {
                profiles = new(StringComparer.OrdinalIgnoreCase);
            }

            if (snapshot.IsOverride)
                profiles[key] = snapshot;
            else
                profiles.Remove(key);

            WriteProfiles(profiles);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private Dictionary<string, ChannelSnapshot> ReadProfiles(out bool malformedEntries)
    {
        malformedEntries = false;
        var profiles = new Dictionary<string, ChannelSnapshot>(StringComparer.OrdinalIgnoreCase);
        string json;
        try
        {
            using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(file);
            json = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return profiles;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The channel profiles map is missing.");

        foreach (var profile in document.RootElement.EnumerateObject())
        {
            try
            {
                var root = NormalizeRoot(profile.Name);
                var channels = profile.Value.Deserialize<string[]>();
                if (channels is null || !profiles.TryAdd(root, ChannelSnapshot.Custom(channels)))
                    malformedEntries = true;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or
                                              NotSupportedException or PathTooLongException)
            {
                malformedEntries = true;
            }
        }

        return profiles;
    }

    private void WriteProfiles(Dictionary<string, ChannelSnapshot> profiles)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, profiles.ToDictionary(profile => profile.Key, profile => profile.Value.Channels));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original persistence failure if temporary cleanup also fails.
            }
        }
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Conda 安装根目录必须使用绝对路径。", nameof(root));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
