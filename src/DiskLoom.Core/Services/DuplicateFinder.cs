using System.Collections.Concurrent;
using System.Security.Cryptography;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class DuplicateFinder
{
    private const int SampleSize = 64 * 1024;

    public async Task<DuplicateResult> FindAsync(
        ScanNode root,
        long minimumFileSize = 1024 * 1024,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new ConcurrentBag<ScanIssue>();
        var candidates = root.Files()
            .Where(file => file.Size >= minimumFileSize)
            .GroupBy(static file => file.FileId == 0
                    ? $"PATH:{file.FullPath}"
                    : $"ID:{file.VolumeSerialNumber:X8}:{file.FileId:X16}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .GroupBy(static file => file.Size)
            .Where(static group => group.Skip(1).Any())
            .SelectMany(static group => group)
            .ToArray();

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
                    var hash = await ComputeSampleHashAsync(file.FullPath, file.Size, token).ConfigureAwait(false);
                    quickHashes.Add((file, hash));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(file.FullPath, exception.Message, exception.GetType().Name));
                }
                finally
                {
                    var current = Interlocked.Increment(ref processed);
                    progress?.Report(new DuplicateProgress("Sampling", current, candidates.LongLength, file.FullPath));
                }
            }).ConfigureAwait(false);

        var fullCandidates = quickHashes
            .GroupBy(static item => (item.File.Size, item.Hash))
            .Where(static group => group.Skip(1).Any())
            .SelectMany(static group => group.Select(static item => item.File))
            .ToArray();

        var fullHashes = new ConcurrentBag<(ScanNode File, string Hash)>();
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
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                    fullHashes.Add((file, Convert.ToHexString(hash)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(file.FullPath, exception.Message, exception.GetType().Name));
                }
                finally
                {
                    var current = Interlocked.Increment(ref processed);
                    progress?.Report(new DuplicateProgress("Verifying", current, fullCandidates.LongLength, file.FullPath));
                }
            }).ConfigureAwait(false);

        var groups = fullHashes
            .GroupBy(static item => (item.File.Size, item.Hash))
            .Where(static group => group.Skip(1).Any())
            .Select(group => new DuplicateGroup(
                group.Key.Hash,
                group.Key.Size,
                group.Select(static item => new DuplicateFile(item.File.FullPath, item.File.Size, item.File.LastWriteUtc))
                    .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(static group => group.ReclaimableSize)
            .ToArray();

        return new DuplicateResult(groups, issues.OrderBy(static issue => issue.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<string> ComputeSampleHashAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            SampleSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
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
