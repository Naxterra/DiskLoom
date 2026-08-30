using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class FileSystemScanner
{
    public async Task<ScanResult> ScanAsync(
        string path,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new ScanOptions();

        var normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"The scan path does not exist: {normalizedPath}");
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var issues = new ConcurrentBag<ScanIssue>();
        var rootInfo = new DirectoryInfo(normalizedPath);
        var root = CreateDirectoryNode(rootInfo);
        var allocationUnitSize = NativeFileSize.GetAllocationUnitSize(normalizedPath);
        var channel = Channel.CreateUnbounded<ScanNode>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        var pendingDirectories = 1L;
        var filesScanned = 0L;
        var directoriesScanned = 0L;
        var bytesScanned = 0L;
        var lastProgressTick = 0L;
        var visitedTargets = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var physicalFiles = new ConcurrentDictionary<FileIdentity, byte>();
        visitedTargets.TryAdd(normalizedPath, 0);

        await channel.Writer.WriteAsync(root, cancellationToken).ConfigureAwait(false);

        var workers = Enumerable.Range(0, Math.Clamp(options.Parallelism, 1, 32))
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var directory in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        ScanDirectory(
                            directory,
                            options,
                            issues,
                            visitedTargets,
                            physicalFiles,
                            allocationUnitSize,
                            channel.Writer,
                            ref pendingDirectories,
                            ref filesScanned,
                            ref directoriesScanned,
                            ref bytesScanned,
                            ref lastProgressTick,
                            stopwatch,
                            progress,
                            cancellationToken);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pendingDirectories) == 0)
                        {
                            channel.Writer.TryComplete();
                        }
                    }
                }
            }, cancellationToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        AggregateDirectories(root);
        SortChildren(root);
        var (extensions, ages) = BuildStatistics(root, DateTimeOffset.UtcNow);
        progress?.Report(new ScanProgress(root.FullPath, filesScanned, directoriesScanned, bytesScanned, stopwatch.Elapsed));

        return new ScanResult(
            root,
            startedUtc,
            DateTimeOffset.UtcNow,
            issues.OrderBy(static issue => issue.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            extensions,
            ages);
    }

    private static void ScanDirectory(
        ScanNode directory,
        ScanOptions options,
        ConcurrentBag<ScanIssue> issues,
        ConcurrentDictionary<string, byte> visitedTargets,
        ConcurrentDictionary<FileIdentity, byte> physicalFiles,
        long allocationUnitSize,
        ChannelWriter<ScanNode> writer,
        ref long pendingDirectories,
        ref long filesScanned,
        ref long directoriesScanned,
        ref long bytesScanned,
        ref long lastProgressTick,
        Stopwatch stopwatch,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<DirectoryEntryData> entries =
                NativeDirectoryReader.TryRead(directory.FullPath, out var nativeEntries)
                    ? nativeEntries
                    : EnumerateManaged(directory.FullPath);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (ShouldExclude(entry.Name, entry.FullPath, entry.Attributes, options))
                    {
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        var child = CreateDirectoryNode(entry);
                        directory.Children.Add(child);

                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            if (IsCloudPlaceholder(entry.ReparsePointTag))
                            {
                                // Cloud Files placeholders are part of the visible folder namespace,
                                // not links to another location. Enumerating their metadata does not
                                // require downloading file contents.
                                if (!visitedTargets.TryAdd(Path.GetFullPath(entry.FullPath), 0))
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                if (!options.FollowReparsePoints)
                                {
                                    continue;
                                }

                                var target = new DirectoryInfo(entry.FullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                                if (string.IsNullOrWhiteSpace(target) || !visitedTargets.TryAdd(Path.GetFullPath(target), 0))
                                {
                                    continue;
                                }
                            }
                        }

                        Interlocked.Increment(ref pendingDirectories);
                        if (!writer.TryWrite(child))
                        {
                            Interlocked.Decrement(ref pendingDirectories);
                        }
                    }
                    else
                    {
                        var size = entry.Length;
                        var storage = !options.CalculateAllocatedSize
                            ? new FileStorageInfo(size, 0, 0, 1)
                            : entry.HasNativeAllocation
                                ? new FileStorageInfo(entry.AllocationSize, 0, entry.FileId, 1)
                                : options.PreciseAllocationSize
                                    ? NativeFileSize.GetStorageInfo(entry.FullPath, size)
                                    : NativeFileSize.GetFastStorageInfo(entry.FullPath, size, entry.Attributes, allocationUnitSize);
                        var allocated = storage.AllocatedSize;
                        var additionalHardLink = false;
                        if (options.DeduplicateHardLinkAllocation && storage.FileId != 0)
                        {
                            additionalHardLink = !physicalFiles.TryAdd(new FileIdentity(storage.VolumeSerialNumber, storage.FileId), 0);
                            if (additionalHardLink)
                            {
                                allocated = 0;
                            }
                        }
                        directory.Children.Add(new ScanNode
                        {
                            Name = entry.Name,
                            FullPath = entry.FullPath,
                            IsDirectory = false,
                            Size = size,
                            AllocatedSize = allocated,
                            FileCount = 1,
                            FolderCount = 0,
                            CreatedUtc = entry.CreationTimeUtc,
                            LastWriteUtc = entry.LastWriteTimeUtc,
                            LastAccessUtc = entry.LastAccessTimeUtc,
                            Attributes = entry.Attributes,
                            VolumeSerialNumber = storage.VolumeSerialNumber,
                            FileId = storage.FileId,
                            HardLinkCount = storage.HardLinkCount,
                            IsAdditionalHardLink = additionalHardLink
                        });
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Add(ref bytesScanned, size);
                    }
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    issues.Add(new ScanIssue(entry.FullPath, exception.Message, exception.GetType().Name));
                }

                ReportProgressIfDue(
                    directory.FullPath,
                    ref lastProgressTick,
                    filesScanned,
                    directoriesScanned,
                    bytesScanned,
                    stopwatch,
                    progress);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            issues.Add(new ScanIssue(directory.FullPath, exception.Message, exception.GetType().Name));
        }
        finally
        {
            Interlocked.Increment(ref directoriesScanned);
        }
    }

    private static IEnumerable<DirectoryEntryData> EnumerateManaged(string path)
    {
        var info = new DirectoryInfo(path);
        foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        }))
        {
            var attributes = entry.Attributes;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            yield return new DirectoryEntryData(
                entry.Name,
                entry.FullName,
                isDirectory,
                isDirectory ? 0 : ((FileInfo)entry).Length,
                0,
                0,
                entry.CreationTimeUtc,
                entry.LastWriteTimeUtc,
                entry.LastAccessTimeUtc,
                attributes,
                (attributes & FileAttributes.ReparsePoint) != 0
                    ? NativeDirectoryReader.GetReparsePointTag(entry.FullName)
                    : 0,
                HasNativeAllocation: false);
        }
    }

    private static bool ShouldExclude(string name, string fullPath, FileAttributes attributes, ScanOptions options)
    {
        if (!options.IncludeHidden && (attributes & FileAttributes.Hidden) != 0)
        {
            return true;
        }

        if (!options.IncludeSystem && (attributes & FileAttributes.System) != 0)
        {
            return true;
        }

        foreach (var pattern in options.ExcludePatterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true) ||
                FileSystemName.MatchesSimpleExpression(pattern, fullPath, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCloudPlaceholder(uint reparsePointTag) =>
        (reparsePointTag & 0xFFFF0FFFu) == 0x9000001Au;

    private static ScanNode CreateDirectoryNode(DirectoryEntryData entry) => new()
    {
        Name = entry.Name,
        FullPath = entry.FullPath,
        IsDirectory = true,
        CreatedUtc = entry.CreationTimeUtc,
        LastWriteUtc = entry.LastWriteTimeUtc,
        LastAccessUtc = entry.LastAccessTimeUtc,
        Attributes = entry.Attributes,
        FileId = entry.FileId
    };

    private static ScanNode CreateDirectoryNode(DirectoryInfo info)
    {
        DateTimeOffset created = default;
        DateTimeOffset written = default;
        DateTimeOffset accessed = default;
        FileAttributes attributes = FileAttributes.Directory;
        try
        {
            created = info.CreationTimeUtc;
            written = info.LastWriteTimeUtc;
            accessed = info.LastAccessTimeUtc;
            attributes = info.Attributes;
        }
        catch
        {
            // Enumeration will report a more useful issue if the folder cannot be opened.
        }

        return new ScanNode
        {
            Name = string.IsNullOrWhiteSpace(info.Name) ? info.FullName : info.Name,
            FullPath = info.FullName,
            IsDirectory = true,
            CreatedUtc = created,
            LastWriteUtc = written,
            LastAccessUtc = accessed,
            Attributes = attributes
        };
    }

    private static void AggregateDirectories(ScanNode root)
    {
        var stack = new Stack<(ScanNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.TryPop(out var item))
        {
            if (!item.Node.IsDirectory)
            {
                continue;
            }

            if (!item.Visited)
            {
                stack.Push((item.Node, true));
                foreach (var child in item.Node.Children)
                {
                    if (child.IsDirectory)
                    {
                        stack.Push((child, false));
                    }
                }
                continue;
            }

            item.Node.Size = item.Node.Children.Sum(static child => child.Size);
            item.Node.AllocatedSize = item.Node.Children.Sum(static child => child.AllocatedSize);
            item.Node.FileCount = item.Node.Children.Sum(static child => child.FileCount);
            item.Node.FolderCount = item.Node.Children.Sum(static child => child.FolderCount + (child.IsDirectory ? 1 : 0));
        }
    }

    private static void SortChildren(ScanNode root)
    {
        foreach (var node in root.DescendantsAndSelf().Where(static node => node.IsDirectory))
        {
            node.Children.Sort(static (left, right) =>
            {
                var size = right.Size.CompareTo(left.Size);
                return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
        }
    }

    private static (IReadOnlyList<ExtensionStatistic> Extensions, IReadOnlyList<AgeStatistic> Ages) BuildStatistics(
        ScanNode root,
        DateTimeOffset now)
    {
        var extensions = new Dictionary<string, ExtensionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var ageLabels = new[]
        {
            "Today",
            "2–7 days",
            "8–30 days",
            "1–6 months",
            "6–12 months",
            "Older than a year"
        };
        var ageSizes = new long[ageLabels.Length];
        var ageCounts = new long[ageLabels.Length];

        foreach (var file in root.Files())
        {
            var extension = string.IsNullOrEmpty(file.Extension) ? "(no extension)" : file.Extension;
            if (!extensions.TryGetValue(extension, out var aggregate))
            {
                aggregate = new ExtensionAccumulator();
                extensions.Add(extension, aggregate);
            }
            aggregate.Size += file.Size;
            aggregate.AllocatedSize += file.AllocatedSize;
            aggregate.FileCount++;

            var age = now - file.LastWriteUtc;
            var ageIndex = age < TimeSpan.FromDays(1) ? 0
                : age < TimeSpan.FromDays(8) ? 1
                : age < TimeSpan.FromDays(31) ? 2
                : age < TimeSpan.FromDays(183) ? 3
                : age < TimeSpan.FromDays(366) ? 4
                : 5;
            ageSizes[ageIndex] += file.Size;
            ageCounts[ageIndex]++;
        }

        var total = Math.Max(1, root.Size);
        var extensionStatistics = extensions
            .Select(pair => new ExtensionStatistic(pair.Key, pair.Value.Size, pair.Value.AllocatedSize, pair.Value.FileCount)
            {
                Percentage = pair.Value.Size * 100d / total
            })
            .OrderByDescending(static statistic => statistic.Size)
            .ToArray();
        var ageStatistics = ageLabels
            .Select((label, index) => new AgeStatistic(label, ageSizes[index], ageCounts[index]))
            .ToArray();
        return (extensionStatistics, ageStatistics);
    }

    private static void ReportProgressIfDue(
        string currentPath,
        ref long lastProgressTick,
        long filesScanned,
        long directoriesScanned,
        long bytesScanned,
        Stopwatch stopwatch,
        IProgress<ScanProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref lastProgressTick);
        if (now - previous < 125 || Interlocked.CompareExchange(ref lastProgressTick, now, previous) != previous)
        {
            return;
        }

        progress.Report(new ScanProgress(
            currentPath,
            Interlocked.Read(ref filesScanned),
            Interlocked.Read(ref directoriesScanned),
            Interlocked.Read(ref bytesScanned),
            stopwatch.Elapsed));
    }

    private static bool IsRecoverable(Exception exception) => exception is
        UnauthorizedAccessException or
        IOException or
        DirectoryNotFoundException or
        FileNotFoundException or
        PathTooLongException or
        System.Security.SecurityException;

    private readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileId);
    private readonly record struct FileStorageInfo(long AllocatedSize, uint VolumeSerialNumber, ulong FileId, uint HardLinkCount);
    private readonly record struct DirectoryEntryData(
        string Name,
        string FullPath,
        bool IsDirectory,
        long Length,
        long AllocationSize,
        ulong FileId,
        DateTimeOffset CreationTimeUtc,
        DateTimeOffset LastWriteTimeUtc,
        DateTimeOffset LastAccessTimeUtc,
        FileAttributes Attributes,
        uint ReparsePointTag,
        bool HasNativeAllocation);

    private sealed class ExtensionAccumulator
    {
        public long Size;
        public long AllocatedSize;
        public long FileCount;
    }

    private static class NativeDirectoryReader
    {
        private const uint FileListDirectory = 0x0001;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int ErrorNoMoreFiles = 18;
        private const int ErrorHandleEof = 38;
        private const int BufferSize = 256 * 1024;
        private const int FileNameOffset = 88;

        public static bool TryRead(string directoryPath, out IReadOnlyList<DirectoryEntryData> entries)
        {
            entries = [];
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var handle = CreateFile(
                directoryPath,
                FileListDirectory,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var results = new List<DirectoryEntryData>();
            var buffer = Marshal.AllocHGlobal(BufferSize);
            try
            {
                var firstCall = true;
                string? previousBatchFirstEntry = null;
                for (var batch = 0; batch < 1_000_000; batch++)
                {
                    var informationClass = firstCall
                        ? FileInfoByHandleClass.FileIdExtdDirectoryRestartInfo
                        : FileInfoByHandleClass.FileIdExtdDirectoryInfo;
                    firstCall = false;
                    if (!GetFileInformationByHandleEx(handle, informationClass, buffer, BufferSize))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is ErrorNoMoreFiles or ErrorHandleEof)
                        {
                            entries = results;
                            return true;
                        }
                        return false;
                    }

                    var offset = 0;
                    string? batchFirstEntry = null;
                    while (offset >= 0 && offset + FileNameOffset <= BufferSize)
                    {
                        var current = IntPtr.Add(buffer, offset);
                        var nextOffset = Marshal.ReadInt32(current, 0);
                        var fileNameLength = Marshal.ReadInt32(current, 60);
                        if (fileNameLength < 0 || fileNameLength > BufferSize - offset - FileNameOffset || (fileNameLength & 1) != 0)
                        {
                            return false;
                        }

                        var name = Marshal.PtrToStringUni(IntPtr.Add(current, FileNameOffset), fileNameLength / 2) ?? string.Empty;
                        batchFirstEntry ??= name;
                        if (name is not ("." or "..") && name.Length > 0)
                        {
                            var attributes = (FileAttributes)(uint)Marshal.ReadInt32(current, 56);
                            var lowFileId = unchecked((ulong)Marshal.ReadInt64(current, 72));
                            var highFileId = unchecked((ulong)Marshal.ReadInt64(current, 80));
                            var rotatedHigh = (highFileId << 17) | (highFileId >> 47);
                            var fileId = lowFileId ^ rotatedHigh;
                            results.Add(new DirectoryEntryData(
                                name,
                                Path.Combine(directoryPath, name),
                                (attributes & FileAttributes.Directory) != 0,
                                Math.Max(0, Marshal.ReadInt64(current, 40)),
                                Math.Max(0, Marshal.ReadInt64(current, 48)),
                                fileId,
                                FromFileTime(Marshal.ReadInt64(current, 8)),
                                FromFileTime(Marshal.ReadInt64(current, 24)),
                                FromFileTime(Marshal.ReadInt64(current, 16)),
                                attributes,
                                unchecked((uint)Marshal.ReadInt32(current, 68)),
                                HasNativeAllocation: true));
                        }

                        if (nextOffset == 0)
                        {
                            break;
                        }
                        if (nextOffset < FileNameOffset || offset + nextOffset <= offset || offset + nextOffset >= BufferSize)
                        {
                            return false;
                        }
                        offset += nextOffset;
                    }

                    if (batchFirstEntry is not null && batchFirstEntry.Equals(previousBatchFirstEntry, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    previousBatchFirstEntry = batchFirstEntry;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static uint GetReparsePointTag(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return 0;
            }

            using var handle = CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal(8);
            try
            {
                return GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, buffer, 8)
                    ? unchecked((uint)Marshal.ReadInt32(buffer, 4))
                    : 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static DateTimeOffset FromFileTime(long fileTime)
        {
            try
            {
                return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
            }
            catch
            {
                return default;
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle fileHandle,
            FileInfoByHandleClass fileInformationClass,
            IntPtr fileInformation,
            int bufferSize);

        private enum FileInfoByHandleClass
        {
            FileAttributeTagInfo = 9,
            FileIdExtdDirectoryInfo = 19,
            FileIdExtdDirectoryRestartInfo = 20
        }
    }

    private static class NativeFileSize
    {
        public static long GetAllocationUnitSize(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return 4096;
            }

            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return 4096;
                }
                if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    root += Path.DirectorySeparatorChar;
                }
                if (GetDiskFreeSpace(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
                {
                    var allocationUnit = (long)sectorsPerCluster * bytesPerSector;
                    if (allocationUnit is >= 512 and <= 1024 * 1024)
                    {
                        return allocationUnit;
                    }
                }
            }
            catch
            {
                // Use the common NTFS allocation unit if the volume cannot be queried.
            }
            return 4096;
        }

        public static FileStorageInfo GetFastStorageInfo(
            string path,
            long length,
            FileAttributes attributes,
            long allocationUnitSize)
        {
            if (length <= 0)
            {
                return new FileStorageInfo(0, 0, 0, 1);
            }

            if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0 && OperatingSystem.IsWindows())
            {
                var low = GetCompressedFileSize(path, out var high);
                if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0)
                {
                    var compressed = ((ulong)high << 32) | low;
                    if (compressed <= long.MaxValue)
                    {
                        return new FileStorageInfo((long)compressed, 0, 0, 1);
                    }
                }
            }

            var unit = Math.Max(512, allocationUnitSize);
            var remainder = length % unit;
            var allocated = remainder == 0 ? length : length + (unit - remainder);
            return new FileStorageInfo(allocated, 0, 0, 1);
        }

        public static FileStorageInfo GetStorageInfo(string path, long fallbackLength)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new FileStorageInfo(fallbackLength, 0, 0, 1);
            }

            try
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.None);
                var hasStandardInfo = GetFileInformationByHandleEx(
                        handle,
                        FileInfoByHandleClass.FileStandardInfo,
                        out var standardInfo,
                        (uint)Marshal.SizeOf<FileStandardInfo>());
                var hasIdentity = GetFileInformationByHandle(handle, out var handleInfo);
                if (hasStandardInfo)
                {
                    var fileId = hasIdentity ? ((ulong)handleInfo.FileIndexHigh << 32) | handleInfo.FileIndexLow : 0;
                    return new FileStorageInfo(
                        Math.Max(0, standardInfo.AllocationSize),
                        hasIdentity ? handleInfo.VolumeSerialNumber : 0,
                        fileId,
                        hasIdentity ? Math.Max(1, handleInfo.NumberOfLinks) : 1);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fall through to the path-based API, which can succeed with less access.
            }

            var low = GetCompressedFileSize(path, out var high);
            if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
            {
                return new FileStorageInfo(fallbackLength, 0, 0, 1);
            }

            var value = ((ulong)high << 32) | low;
            return new FileStorageInfo(value > long.MaxValue ? fallbackLength : (long)value, 0, 0, 1);
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpace(
            string rootPathName,
            out uint sectorsPerCluster,
            out uint bytesPerSector,
            out uint numberOfFreeClusters,
            out uint totalNumberOfClusters);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle fileHandle,
            FileInfoByHandleClass fileInformationClass,
            out FileStandardInfo fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle fileHandle,
            out ByHandleFileInformation fileInformation);

        private enum FileInfoByHandleClass
        {
            FileStandardInfo = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileStandardInfo
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
            [MarshalAs(UnmanagedType.U1)] public bool Directory;
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
    }
}
