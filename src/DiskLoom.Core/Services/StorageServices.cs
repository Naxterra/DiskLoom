using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskLoom.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskLoom.Core.Services;

public sealed class DriveService
{
    public IReadOnlyList<DriveSummary> GetDrives()
    {
        var results = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var ready = drive.IsReady;
                results.Add(new DriveSummary(
                    drive.Name,
                    ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})" : drive.Name,
                    ready ? drive.DriveFormat : string.Empty,
                    drive.DriveType,
                    ready ? drive.TotalSize : 0,
                    ready ? drive.AvailableFreeSpace : 0,
                    ready));
            }
            catch (IOException)
            {
                results.Add(new DriveSummary(drive.Name, drive.Name, string.Empty, drive.DriveType, 0, 0, false));
            }
        }

        return results.OrderBy(static drive => drive.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class StorageInsightService
{
    public IReadOnlyList<StorageInsight> Analyze(ScanResult result, bool useAllocated = false)
    {
        var now = DateTimeOffset.UtcNow;
        var largeFiles = new PriorityQueue<ScanNode, long>(21);
        var staleFiles = new PriorityQueue<ScanNode, long>(21);
        long tempCount = 0;
        long tempBytes = 0;
        long emptyCount = 0;

        foreach (var file in result.Root.Files())
        {
            if (DuplicateFinder.IsCloudOnly(file))
            {
                continue;
            }

            if (file.Size == 0)
            {
                emptyCount++;
            }

            if (file.IsAdditionalHardLink)
            {
                continue;
            }

            var bytes = useAllocated ? file.AllocatedSize : file.Size;
            if (bytes >= 1024L * 1024 * 1024)
            {
                largeFiles.Enqueue(file, bytes);
                if (largeFiles.Count > 20)
                {
                    largeFiles.Dequeue();
                }
            }

            if (bytes >= 100L * 1024 * 1024 && now - file.LastWriteUtc >= TimeSpan.FromDays(730))
            {
                staleFiles.Enqueue(file, bytes);
                if (staleFiles.Count > 20)
                {
                    staleFiles.Dequeue();
                }
            }

            if (file.Extension is ".tmp" or ".temp" or ".dmp")
            {
                tempCount++;
                tempBytes += bytes;
            }
        }

        var insights = new List<StorageInsight>();
        while (largeFiles.TryDequeue(out var file, out var bytes))
        {
            insights.Add(new StorageInsight(
                "LargeFile",
                "Very large file",
                $"{ByteFormatter.Format(bytes)} in one file",
                file.FullPath,
                bytes,
                InsightSeverity.Opportunity));
        }

        while (staleFiles.TryDequeue(out var file, out var bytes))
        {
            insights.Add(new StorageInsight(
                "StaleFile",
                "Large file untouched for 2+ years",
                $"Last changed {file.LastWriteUtc.LocalDateTime:d}",
                file.FullPath,
                bytes,
                InsightSeverity.Opportunity,
                ReferenceDate: file.LastWriteUtc));
        }

        if (tempCount > 0)
        {
            insights.Add(new StorageInsight(
                "TemporaryFiles",
                "Temporary and dump files",
                $"{tempCount:N0} candidates; review before recycling",
                result.Root.FullPath,
                tempBytes,
                InsightSeverity.Attention,
                tempCount));
        }

        if (emptyCount > 0)
        {
            insights.Add(new StorageInsight(
                "EmptyFiles",
                "Empty files",
                $"{emptyCount:N0} zero-byte files",
                result.Root.FullPath,
                0,
                InsightSeverity.Info,
                emptyCount));
        }

        return insights.OrderByDescending(static insight => insight.PotentialBytes).ToArray();
    }
}

// Windows silently drops a trailing dot or space from every path segment, so on a drive written by Linux or macOS
// "Backup " means "Backup" to ordinary Win32 calls. These helpers keep file operations on the item actually named.
public static class WindowsPaths
{
    // True when a segment ends in a dot or space; Explorer's engine and the Recycle Bin cannot address such items.
    public static bool HasTrimmedSegment(string path) =>
        StripExtendedPrefix(path)
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is not "." and not ".." && (segment.EndsWith('.') || segment.EndsWith(' ')));

    // Paths with the \\?\ prefix reach the file system unchanged, without that trimming.
    public static string ToExtended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    public static string StripExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    // True for a drive root and for a volume mounted in a folder, such as C:\Mount\USB. Deleting or moving one
    // would empty a whole volume, so the file operations refuse it.
    public static bool IsVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = StripExtendedPrefix(ScanPaths.Normalize(StripExtendedPrefix(path)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var plain = normalized.TrimEnd('\\', '/');
        if (plain.Length == 2 && char.IsAsciiLetter(plain[0]) && plain[1] == ':')
        {
            return true;
        }

        var buffer = new char[Math.Max(1024, normalized.Length + 16)];
        var volume = GetVolumePathName(ToExtended(normalized), buffer, (uint)buffer.Length) ||
                     GetVolumePathName(normalized, buffer, (uint)buffer.Length)
            ? new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0')))
            : Path.GetPathRoot(normalized);
        return !string.IsNullOrEmpty(volume) &&
               string.Equals(
                   StripExtendedPrefix(volume).TrimEnd('\\', '/'),
                   plain,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNonCloudReparsePoint(string extendedPath, FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0 &&
        !FileSystemScanner.IsCloudPlaceholder(FileSystemScanner.GetReparsePointTag(extendedPath));

    internal static string ToNormalizedExtended(string path) =>
        Path.TrimEndingDirectorySeparator(ToExtended(ScanPaths.Normalize(StripExtendedPrefix(path))));

    // Returns the first mounted volume root found inside a directory tree, or null if none exist.
    public static string? FindContainedVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string extendedRoot;
        try
        {
            extendedRoot = ToNormalizedExtended(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!Directory.Exists(extendedRoot))
        {
            return null;
        }

        var rootAttributes = File.GetAttributes(extendedRoot);
        if (IsNonCloudReparsePoint(extendedRoot, rootAttributes))
        {
            return null;
        }

        var pending = new Stack<string>();
        pending.Push(extendedRoot);
        while (pending.TryPop(out var current))
        {
            IEnumerable<DirectoryInfo> directories;
            try
            {
                directories = new DirectoryInfo(current).EnumerateDirectories("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (IsNonCloudReparsePoint(directory.FullName, directory.Attributes))
                {
                    var plainChild = StripExtendedPrefix(directory.FullName);
                    if (IsVolumeRoot(plainChild))
                    {
                        return plainChild;
                    }
                    continue;
                }

                pending.Push(directory.FullName);
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);
}

public sealed class FileOperationService
{
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteAttributes = 0x00000100;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int ErrorAccessDenied = 5;

    // Recycles with Explorer's engine and lets it warn before anything would be deleted for good instead. Removable
    // drives have no Recycle Bin and items larger than the bin do not fit; the previous call removed those silently.
    public async Task RecycleAsync(string path, nint ownerWindow = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WindowsPaths.HasTrimmedSegment(path))
        {
            throw new IOException($"The name ends with a dot or space, which the Recycle Bin cannot handle safely: {path}");
        }
        RefuseVolumeRoot(path);
        _ = File.GetAttributes(WindowsPaths.ToNormalizedExtended(path));

        var result = await RunShellFileOperationAsync(FoDelete, [path], destinationFolder: null, ownerWindow,
            FofAllowUndo | FofNoConfirmation | FofWantNukeWarning).ConfigureAwait(false);
        if (result.Aborted)
        {
            throw new OperationCanceledException("Moving the item to the Recycle Bin was canceled.");
        }
        if (result.ErrorCode != 0)
        {
            throw new IOException($"Windows reported error {result.ErrorCode} while moving the item to the Recycle Bin: {path}");
        }
    }

    // Test the Windows delete right without asking for delete-on-close or changing metadata.
    // Check children as well, because a readable folder can contain administrator-owned files.
    public bool PermanentDeletionRequiresElevation(string path, CancellationToken cancellationToken = default)
    {
        if (WindowsPaths.IsVolumeRoot(path))
        {
            return false;
        }

        var pending = new Stack<string>();
        pending.Push(WindowsPaths.ToNormalizedExtended(path));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(item);
                var directory = (attributes & FileAttributes.Directory) != 0;
                var access = DeleteAccess;
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    access |= WriteAttributes;
                }
                using (var handle = OpenFileForDeletion(item, access, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0))
                {
                    if (handle.IsInvalid && Marshal.GetLastWin32Error() == ErrorAccessDenied)
                    {
                        return true;
                    }
                }

                if (directory && !WindowsPaths.IsNonCloudReparsePoint(item, attributes))
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(item))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pending.Push(child);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                // A missing entry or a sharing violation is not an administrator permissions request.
            }
        }
        return false;
    }

    public Task DeletePermanentlyAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefuseVolumeRoot(path);
        if (WindowsPaths.FindContainedVolumeRoot(path) is { } nestedVolume)
        {
            throw new InvalidOperationException($"DiskLoom never deletes or moves a drive or volume root: {nestedVolume}");
        }
        // The extended path deletes exactly the named item, also when its name ends in a dot or space.
        var extended = WindowsPaths.ToNormalizedExtended(path);
        var attributes = File.GetAttributes(extended);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            DeleteFile(extended, attributes);
            return;
        }

        // Walk the tree bottom up so read-only children get the same treatment as selected files.
        // Reparse directories are removed as entries, without enumerating their targets.
        var pending = new Stack<(string Path, bool RemoveDirectory)>();
        pending.Push((extended, false));
        IOException? firstFailure = null;
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (item.RemoveDirectory)
                {
                    DeleteDirectoryEntry(item.Path);
                    continue;
                }

                var childAttributes = File.GetAttributes(item.Path);
                if ((childAttributes & FileAttributes.Directory) == 0)
                {
                    DeleteFile(item.Path, childAttributes);
                }
                else if (WindowsPaths.IsNonCloudReparsePoint(item.Path, childAttributes))
                {
                    RefuseVolumeRoot(item.Path);
                    DeleteDirectoryEntry(item.Path, childAttributes);
                }
                else
                {
                    pending.Push((item.Path, true));
                    foreach (var child in Directory.EnumerateFileSystemEntries(item.Path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pending.Push((child, false));
                    }
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Another process already removed this entry.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                firstFailure ??= new IOException($"{WindowsPaths.StripExtendedPrefix(item.Path)}: {exception.Message}", exception);
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }, cancellationToken);

    private static void DeleteDirectoryEntry(string path, FileAttributes? knownAttributes = null)
    {
        var attributes = knownAttributes ?? File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path);
    }

    private static void DeleteFile(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            File.Delete(path);
            return;
        }

        // Delete this name without clearing ReadOnly, which would also change every retained hard link.
        // Open the reparse point itself and let Windows enforce delete permissions and sharing rules.
        const int fileDispositionInfoEx = 21;
        const uint deleteIgnoringReadOnly = 0x00000001 | 0x00000010;
        const int errorInvalidFunction = 1;
        const int errorNotSupported = 50;
        const int errorInvalidParameter = 87;
        int win32Error;
        uint numberOfLinks = 0;
        using (var handle = OpenFileForDeletion(path, DeleteAccess | WriteAttributes, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, OpenReparsePoint, 0))
        {
            var disposition = deleteIgnoringReadOnly;
            if (!handle.IsInvalid && SetFileInformationByHandle(handle, fileDispositionInfoEx, in disposition, sizeof(uint)))
            {
                return;
            }
            win32Error = Marshal.GetLastWin32Error();
            if (!handle.IsInvalid && GetFileInformationByHandle(handle, out var info))
            {
                numberOfLinks = info.NumberOfLinks;
            }
        }

        // Filesystems without hard links (such as FAT32 and exFAT on USB drives) reject FileDispositionInfoEx;
        // fall back to clearing ReadOnly before deleting when there is only one link.
        if (win32Error is errorInvalidFunction or errorNotSupported or errorInvalidParameter && numberOfLinks <= 1)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return;
        }

        var error = new Win32Exception(win32Error);
        throw new IOException($"{WindowsPaths.StripExtendedPrefix(path)}: {error.Message}", error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFileForDeletion(string path, uint access, FileShare sharing, nint security,
        uint creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, in uint information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileLinkInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileLinkInfo
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

    // Explorer and the shell trim a name ending in a dot or space and would show, open or describe its twin (the
    // item without the dot or space). Such items are shown by opening the folder that holds them, and opening them
    // or showing their properties is refused.
    public void ShowInExplorer(string path)
    {
        var arguments = WindowsPaths.HasTrimmedSegment(path)
            ? $"\"{Path.GetDirectoryName(path)}\""
            : File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    public void Open(string path)
    {
        RefuseTrimmedName(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool ShowProperties(nint ownerWindow, string path)
    {
        RefuseTrimmedName(path);
        return OperatingSystem.IsWindows() && SHObjectProperties(ownerWindow, ShopFilePath, path, null);
    }

    private static void RefuseTrimmedName(string path)
    {
        if (WindowsPaths.HasTrimmedSegment(path))
        {
            throw new IOException($"The name ends with a dot or space, which Windows would open as a different item: {path}");
        }
    }

    // Copies or moves with Explorer's own engine: progress dialog, name-conflict prompts,
    // cross-volume moves and Undo in Explorer all behave exactly as in File Explorer.
    public Task<ShellFileOperationResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, nint ownerWindow) =>
        RunShellFileOperationAsync(FoCopy, sources, destinationFolder, ownerWindow, FofAllowUndo | FofNoConfirmMkdir);

    public Task<ShellFileOperationResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, nint ownerWindow) =>
        RunShellFileOperationAsync(FoMove, sources, destinationFolder, ownerWindow, FofAllowUndo | FofNoConfirmMkdir);

    private static Task<ShellFileOperationResult> RunShellFileOperationAsync(uint operation, IReadOnlyList<string> sources, string? destinationFolder, nint ownerWindow, ushort flags)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            return Task.FromResult(new ShellFileOperationResult(0, Aborted: false));
        }
        // Explorer's engine trims names like the rest of Win32 and would act on a different item.
        if (sources.FirstOrDefault(WindowsPaths.HasTrimmedSegment) is { } trimmedPath)
        {
            throw new IOException($"The name ends with a dot or space, which Windows cannot copy, move or recycle safely: {trimmedPath}");
        }
        var normalizedSources = sources.Select(ScanPaths.Normalize).ToArray();
        if (operation is FoMove or FoDelete)
        {
            foreach (var source in normalizedSources)
            {
                RefuseVolumeRoot(source);
            }
        }
        if (operation != FoDelete)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
            if (WindowsPaths.HasTrimmedSegment(destinationFolder))
            {
                throw new IOException($"The destination name ends with a dot or space, which Windows cannot copy or move into safely: {destinationFolder}");
            }
            if (!Directory.Exists(destinationFolder))
            {
                throw new DirectoryNotFoundException(destinationFolder);
            }
        }

        var completion = new TaskCompletionSource<ShellFileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The shell's progress and conflict dialogs need a single-threaded apartment, and the
        // call blocks until the user has finished with them, so it gets its own thread.
        var thread = new Thread(() =>
        {
            try
            {
                if (operation is FoMove or FoDelete)
                {
                    foreach (var source in normalizedSources)
                    {
                        if (WindowsPaths.FindContainedVolumeRoot(source) is { } nestedVolume)
                        {
                            throw new InvalidOperationException($"DiskLoom never deletes or moves a drive or volume root: {nestedVolume}");
                        }
                    }
                }
                var fileOperation = new ShFileOperation
                {
                    Owner = ownerWindow,
                    Function = operation,
                    // Both lists are double-null-terminated; the marshaller adds the final null.
                    From = string.Join('\0', normalizedSources) + '\0',
                    To = destinationFolder is null ? null : ScanPaths.Normalize(destinationFolder) + '\0',
                    Flags = flags
                };
                var error = SHFileOperation(ref fileOperation);
                completion.TrySetResult(new ShellFileOperationResult(error, fileOperation.AnyOperationsAborted));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "DiskLoom shell file operation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RefuseVolumeRoot(string path)
    {
        if (WindowsPaths.IsVolumeRoot(path))
        {
            throw new InvalidOperationException($"DiskLoom never deletes or moves a drive or volume root: {path}");
        }
    }

    private const uint FoMove = 0x0001;
    private const uint FoCopy = 0x0002;
    private const uint FoDelete = 0x0003;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofWantNukeWarning = 0x4000;
    private const ushort FofNoConfirmMkdir = 0x0200;
    private const ushort FofAllowUndo = 0x0040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public nint Owner;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation fileOperation);

    private const uint ShopFilePath = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(nint ownerWindow, uint objectType, string objectName, string? propertyPage);
}

// ErrorCode is the shell's return value (0 = success); Aborted = the user canceled part of it.
public sealed record ShellFileOperationResult(int ErrorCode, bool Aborted)
{
    public bool Succeeded => ErrorCode == 0 && !Aborted;
}

public static class ByteFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
