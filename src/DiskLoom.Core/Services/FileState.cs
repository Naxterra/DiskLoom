using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLoom.Core.Services;

// Size and times of a file, read from a handle that is opened for attributes only, so neither the contents nor
// an online-only cloud file are touched. NTFS updates the change time on every write, and unlike the modification
// time no ordinary API can set it back, so it also reveals a file rewritten with the same size and date.
internal readonly record struct FileState(long Size, DateTime LastWriteUtc, DateTime? ChangeUtc, bool IsDirectory, bool DeletePending)
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;

    public static FileState? Read(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        return handle.IsInvalid ? null : Read(handle);
    }

    public static FileState? Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>()) ||
            !GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo standard, (uint)Marshal.SizeOf<FileStandardInfo>()))
        {
            return null;
        }

        // File systems without a change time (FAT, for example) report zero.
        return new FileState(
            standard.EndOfFile,
            DateTime.FromFileTimeUtc(basic.LastWriteTime),
            basic.ChangeTime == 0 ? null : DateTime.FromFileTimeUtc(basic.ChangeTime),
            standard.Directory != 0,
            standard.DeletePending != 0);
    }

    // The volume serial number and file ID of a file or directory, after following any link to its final target.
    // Two paths with the same identity are the same object on disk.
    public static (uint VolumeSerialNumber, ulong FileId)? ReadIdentity(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileStandardInfo info, uint size);
}
