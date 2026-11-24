using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CondaManager.Core;

/// <summary>Identifies a Windows directory by its volume and filesystem file index.</summary>
public readonly record struct WindowsDirectoryIdentity(uint VolumeSerialNumber, ulong FileIndex)
{
    private const uint FileShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x10;

    public static WindowsDirectoryIdentity Get(string absoluteDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteDirectory);
        if (!Path.IsPathFullyQualified(absoluteDirectory))
            throw new ArgumentException("目录路径必须是绝对路径。", nameof(absoluteDirectory));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("目录身份检查仅支持 Windows。");

        // FILE_FLAG_BACKUP_SEMANTICS permits opening an existing directory.
        using var handle = Native.CreateFileW(absoluteDirectory, 0, FileShareReadWriteDelete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            ThrowOpenFailure(absoluteDirectory, Marshal.GetLastWin32Error());

        if (!Native.GetFileInformationByHandle(handle, out var information))
            ThrowOpenFailure(absoluteDirectory, Marshal.GetLastWin32Error());
        if ((information.FileAttributes & FileAttributeDirectory) == 0)
            throw new IOException($"目标不是目录，无法比较身份：{absoluteDirectory}");

        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if (information.VolumeSerialNumber == 0 || fileIndex == 0)
            throw new IOException($"文件系统未提供可比较的目录身份：{absoluteDirectory}");

        return new WindowsDirectoryIdentity(information.VolumeSerialNumber, fileIndex);
    }

    public static bool IsSameOrAncestor(string candidateDirectory, string descendantDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descendantDirectory);
        if (!Path.IsPathFullyQualified(descendantDirectory))
            throw new ArgumentException("目录路径必须是绝对路径。", nameof(descendantDirectory));

        var candidateIdentity = Get(candidateDirectory);
        for (DirectoryInfo? current = new(descendantDirectory); current is not null; current = current.Parent)
        {
            if (Get(current.FullName) == candidateIdentity)
                return true;
        }
        return false;
    }

    private static void ThrowOpenFailure(string path, int errorCode)
    {
        var cause = new Win32Exception(errorCode);
        if (errorCode == 5) // ERROR_ACCESS_DENIED
            throw new UnauthorizedAccessException($"无法读取目录身份：{path}", cause);
        throw new IOException($"无法读取目录身份：{path}（{cause.Message}）", cause);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle",
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle,
            out ByHandleFileInformation information);
    }
}
