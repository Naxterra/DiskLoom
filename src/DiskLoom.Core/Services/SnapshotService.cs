using System.IO.Compression;
using System.Text.Json;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class SnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task SaveAsync(ScanResult result, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var snapshot = Create(result);

        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false);
        await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanSnapshot> LoadAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await using var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        var snapshot = await JsonSerializer.DeserializeAsync<ScanSnapshot>(gzip, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The snapshot file is empty or invalid.");

        // DiskLoom 0.1.19 and earlier stored drive-root paths relative to the process working
        // directory, so the root entry was saved as "..", "..\.." and so on instead of ".".
        if (snapshot.Entries.Count > 0 && snapshot.Entries[0].RelativePath != ".")
        {
            throw new InvalidDataException(
                "This snapshot was saved by DiskLoom 0.1.19 or earlier, which stored drive-root paths incorrectly. Save a new snapshot to compare against.");
        }

        return snapshot;
    }

    public IReadOnlyList<SnapshotChange> Compare(ScanSnapshot older, ScanSnapshot newer, bool includeUnchanged = false)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        var before = older.Entries.ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var after = newer.Entries.ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var allPaths = before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var changes = new List<SnapshotChange>();

        foreach (var path in allPaths)
        {
            var hadOld = before.TryGetValue(path, out var oldEntry);
            var hasNew = after.TryGetValue(path, out var newEntry);
            var kind = (hadOld, hasNew) switch
            {
                (false, true) => ChangeKind.Added,
                (true, false) => ChangeKind.Removed,
                _ when oldEntry!.Size != newEntry!.Size || oldEntry.AllocatedSize != newEntry.AllocatedSize => ChangeKind.Changed,
                _ => ChangeKind.Unchanged
            };

            if (includeUnchanged || kind != ChangeKind.Unchanged)
            {
                changes.Add(new SnapshotChange(
                    path,
                    oldEntry?.Size ?? 0,
                    newEntry?.Size ?? 0,
                    oldEntry?.AllocatedSize ?? 0,
                    newEntry?.AllocatedSize ?? 0,
                    kind));
            }
        }

        return changes
            .OrderByDescending(static change => Math.Abs(change.SizeDelta))
            .ThenBy(static change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ScanSnapshot Create(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rootPath = result.Root.FullPath;
        return new ScanSnapshot(
            rootPath,
            DateTimeOffset.UtcNow,
            result.Root.DescendantsAndSelf()
                .Select(node => new SnapshotEntry(
                    GetRelativePath(rootPath, node.FullPath),
                    node.IsDirectory,
                    node.Size,
                    node.AllocatedSize,
                    node.FileCount))
                .ToArray());
    }

    // Scanned paths are built by appending names to the root path, so slicing is exact and,
    // unlike Path.GetRelativePath on a trimmed "C:", never depends on the working directory.
    private static string GetRelativePath(string rootPath, string fullPath)
    {
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(rootPath, fullPath);
        }

        if (fullPath.Length == rootPath.Length)
        {
            return ".";
        }

        if (Path.EndsInDirectorySeparator(rootPath))
        {
            return fullPath[rootPath.Length..];
        }

        return fullPath[rootPath.Length] is '\\' or '/'
            ? fullPath[(rootPath.Length + 1)..]
            : Path.GetRelativePath(rootPath, fullPath);
    }
}
