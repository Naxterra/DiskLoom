using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskLoom.Core.Models;

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
    public IReadOnlyList<StorageInsight> Analyze(ScanResult result)
    {
        var files = result.Root.Files().ToArray();
        var now = DateTimeOffset.UtcNow;
        var insights = new List<StorageInsight>();

        foreach (var file in files.Where(static file => file.Size >= 1024L * 1024 * 1024).OrderByDescending(static file => file.Size).Take(20))
        {
            insights.Add(new StorageInsight(
                "LargeFile",
                "Very large file",
                $"{FormatBytes(file.Size)} in one file",
                file.FullPath,
                file.Size,
                InsightSeverity.Opportunity));
        }

        foreach (var file in files
                     .Where(file => file.Size >= 100L * 1024 * 1024 && now - file.LastWriteUtc >= TimeSpan.FromDays(730))
                     .OrderByDescending(static file => file.Size)
                     .Take(20))
        {
            insights.Add(new StorageInsight(
                "StaleFile",
                "Large file untouched for 2+ years",
                $"Last changed {file.LastWriteUtc.LocalDateTime:d}",
                file.FullPath,
                file.Size,
                InsightSeverity.Opportunity,
                ReferenceDate: file.LastWriteUtc));
        }

        var tempFiles = files.Where(static file => file.Extension is ".tmp" or ".temp" or ".dmp").ToArray();
        if (tempFiles.Length > 0)
        {
            insights.Add(new StorageInsight(
                "TemporaryFiles",
                "Temporary and dump files",
                $"{tempFiles.Length:N0} candidates; review before recycling",
                result.Root.FullPath,
                tempFiles.Sum(static file => file.Size),
                InsightSeverity.Attention,
                tempFiles.LongLength));
        }

        var emptyFiles = files.Count(static file => file.Size == 0);
        if (emptyFiles > 0)
        {
            insights.Add(new StorageInsight(
                "EmptyFiles",
                "Empty files",
                $"{emptyFiles:N0} zero-byte files",
                result.Root.FullPath,
                0,
                InsightSeverity.Info,
                emptyFiles));
        }

        return insights.OrderByDescending(static insight => insight.PotentialBytes).ToArray();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
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
        var plain = StripExtendedPrefix(path.Trim());
        if (plain.Length is 2 or 3 && char.IsAsciiLetter(plain[0]) && plain[1] == ':' && (plain.Length == 2 || plain[2] is '\\' or '/'))
        {
            return true;
        }

        var buffer = new char[1024];
        var volume = GetVolumePathName(plain, buffer, (uint)buffer.Length)
            ? new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0')))
            : Path.GetPathRoot(plain);
        return !string.IsNullOrEmpty(volume) &&
               string.Equals(Path.TrimEndingDirectorySeparator(volume), Path.TrimEndingDirectorySeparator(plain), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);
}

public sealed class FileOperationService
{
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
        var extended = WindowsPaths.ToExtended(path);
        if (!File.Exists(extended) && !Directory.Exists(extended))
        {
            throw new FileNotFoundException("The selected item no longer exists.", path);
        }

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

    public Task DeletePermanentlyAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefuseVolumeRoot(path);
        // The extended path deletes exactly the named item, also when its name ends in a dot or space.
        var extended = WindowsPaths.ToExtended(path);
        if (File.Exists(extended))
        {
            File.Delete(extended);
        }
        else if (Directory.Exists(extended))
        {
            Directory.Delete(extended, recursive: true);
        }
        else
        {
            throw new FileNotFoundException("The selected item no longer exists.", path);
        }
    }, cancellationToken);

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
        if (operation is FoMove or FoDelete)
        {
            foreach (var source in sources)
            {
                RefuseVolumeRoot(source);
            }
        }
        if (operation != FoDelete)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
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
                var fileOperation = new ShFileOperation
                {
                    Owner = ownerWindow,
                    Function = operation,
                    // Both lists are double-null-terminated; the marshaller adds the final null.
                    From = string.Join('\0', sources.Select(Path.GetFullPath)) + '\0',
                    To = destinationFolder is null ? null : Path.GetFullPath(destinationFolder) + '\0',
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
