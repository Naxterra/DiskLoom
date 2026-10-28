using System.Collections.Concurrent;
using System.Security.Cryptography;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class DuplicateFinder
{
    private const int SampleSize = 64 * 1024;

    // Online-only cloud files (Nextcloud, OneDrive, ...): reading their contents downloads them.
    private const FileAttributes CloudOnlyAttributes =
        FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    public static bool IsCloudOnly(ScanNode file) => (file.Attributes & CloudOnlyAttributes) != 0;

    // Groups that would lose their last copy: a copy is about to be deleted, and no other copy is still listed, still
    // on disk and unchanged since the check. Deleting duplicates must keep at least one intact copy, so a selection
    // like "select all, delete" is refused, and so is deleting the last copy after another program removed the rest.
    public static IReadOnlyList<DuplicateGroup> GroupsLosingEveryCopy(
        IEnumerable<DuplicateGroup> groups,
        IEnumerable<string> remainingPaths,
        IEnumerable<string> deletingPaths)
    {
        var remaining = remainingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleting = deletingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups
            .Where(group =>
                group.Files.Any(file => deleting.Contains(file.Path)) &&
                !group.Files.Any(file => remaining.Contains(file.Path) && !deleting.Contains(file.Path) && IsUnchanged(file)))
            .ToArray();
    }

    public async Task<DuplicateResult> FindAsync(
        ScanNode root,
        long minimumFileSize = 1024 * 1024,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new ConcurrentBag<ScanIssue>();
        var sized = root.Files().Where(file => file.Size >= minimumFileSize).ToArray();
        var skippedCloudFiles = sized.LongCount(IsCloudOnly);
        var candidates = sized
            .Where(static file => !IsCloudOnly(file))
            .GroupBy(static file => file.FileId == 0
                    ? $"PATH:{file.FullPath}"
                    : $"ID:{file.VolumeSerialNumber:X8}:{file.FileId:X16}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .GroupBy(static file => file.Size)
            .Where(static group => group.Skip(1).Any())
            .SelectMany(static group => group)
            .ToArray();

        var lastReportTick = 0L;
        void ReportProgress(string phase, long current, long total, string path)
        {
            if (progress is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref lastReportTick);
            if (current != total &&
                (now - previous < 100 || Interlocked.CompareExchange(ref lastReportTick, now, previous) != previous))
            {
                return;
            }

            progress.Report(new DuplicateProgress(phase, current, total, path));
        }

        var quickHashes = new ConcurrentBag<(ScanNode File, string Hash)>();
        var processed = 0L;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
                CancellationToken = cancellationToken
            },
            async (file, token) =>
            {
                try
                {
                    var hash = await ComputeSampleHashAsync(file, token).ConfigureAwait(false);
                    quickHashes.Add((file, hash));
                }
                catch (FileChangedException exception)
                {
                    issues.Add(new ScanIssue(file.FullPath, exception.Message, "ChangedSinceScan"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(file.FullPath, exception.Message, exception.GetType().Name));
                }
                finally
                {
                    ReportProgress("Sampling", Interlocked.Increment(ref processed), candidates.LongLength, file.FullPath);
                }
            }).ConfigureAwait(false);

        var fullCandidates = quickHashes
            .GroupBy(static item => (item.File.Size, item.Hash))
            .Where(static group => group.Skip(1).Any())
            .SelectMany(static group => group.Select(static item => item.File))
            .ToArray();

        var fullHashes = new ConcurrentBag<(ScanNode File, string Hash, DateTime? ChangeUtc)>();
        processed = 0;
        await Parallel.ForEachAsync(
            fullCandidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                CancellationToken = cancellationToken
            },
            async (file, token) =>
            {
                try
                {
                    await using var stream = new FileStream(
                        WindowsPaths.ToExtended(file.FullPath),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    EnsureUnchanged(stream, file);
                    var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                    // The share mode allows writers, so make sure nothing changed while hashing.
                    EnsureUnchanged(stream, file);
                    fullHashes.Add((file, Convert.ToHexString(hash), FileState.Read(stream.SafeFileHandle)?.ChangeUtc));
                }
                catch (FileChangedException exception)
                {
                    issues.Add(new ScanIssue(file.FullPath, exception.Message, "ChangedSinceScan"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(file.FullPath, exception.Message, exception.GetType().Name));
                }
                finally
                {
                    ReportProgress("Verifying", Interlocked.Increment(ref processed), fullCandidates.LongLength, file.FullPath);
                }
            }).ConfigureAwait(false);

        var groups = fullHashes
            .GroupBy(static item => (item.File.Size, item.Hash))
            .Where(static group => group.Skip(1).Any())
            .Select(group => new DuplicateGroup(
                group.Key.Hash,
                group.Key.Size,
                group.Select(static item => new DuplicateFile(item.File.FullPath, item.File.Size, item.File.LastWriteUtc, item.ChangeUtc))
                    .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(static group => group.ReclaimableSize)
            .ToArray();

        return new DuplicateResult(groups, issues.OrderBy(static issue => issue.Path, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            SkippedCloudFiles = skippedCloudFiles
        };
    }

    // Groups report the scanned size and date; a file that no longer has them is skipped instead.
    private static void EnsureUnchanged(FileStream stream, ScanNode file)
    {
        var lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        if (stream.Length != file.Size || (file.LastWriteUtc != default && lastWrite != file.LastWriteUtc.UtcDateTime))
        {
            throw new FileChangedException(file.FullPath);
        }
    }

    // True when the file still has the size, modification time and change time the duplicate check recorded, so a
    // later delete acts on the same content the user reviewed. The change time also catches a file rewritten with
    // the same size whose modification time was set back, as some copy and sync tools do.
    public static bool IsUnchanged(DuplicateFile file) =>
        FileState.Read(WindowsPaths.ToExtended(file.Path)) is { IsDirectory: false, DeletePending: false } current &&
        current.Size == file.Size &&
        current.LastWriteUtc == file.LastWriteUtc.UtcDateTime &&
        (file.ChangeUtc is null || current.ChangeUtc == file.ChangeUtc);

    private sealed class FileChangedException(string path)
        : IOException($"The file changed after the scan and was skipped; rescan to include it: {path}");

    private static async Task<string> ComputeSampleHashAsync(ScanNode file, CancellationToken cancellationToken)
    {
        var length = file.Size;
        await using var stream = new FileStream(
            WindowsPaths.ToExtended(file.FullPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            SampleSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        EnsureUnchanged(stream, file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SampleSize];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        hash.AppendData(buffer.AsSpan(0, read));

        if (length > SampleSize)
        {
            stream.Position = Math.Max(0, length - SampleSize);
            read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, read));
        }

        hash.AppendData(BitConverter.GetBytes(length));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
