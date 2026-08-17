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
        var rootPrefix = result.Root.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var entries = result.Root.DescendantsAndSelf()
            .Select(node => new SnapshotEntry(
                Path.GetRelativePath(rootPrefix, node.FullPath),
                node.IsDirectory,
                node.Size,
                node.AllocatedSize,
                node.FileCount))
            .ToArray();
        var snapshot = new ScanSnapshot(result.Root.FullPath, DateTimeOffset.UtcNow, entries);

        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false);
        await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanSnapshot> LoadAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await using var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        return await JsonSerializer.DeserializeAsync<ScanSnapshot>(gzip, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The snapshot file is empty or invalid.");
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
        var rootPrefix = result.Root.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new ScanSnapshot(
            result.Root.FullPath,
            DateTimeOffset.UtcNow,
            result.Root.DescendantsAndSelf()
                .Select(node => new SnapshotEntry(
                    Path.GetRelativePath(rootPrefix, node.FullPath),
                    node.IsDirectory,
                    node.Size,
                    node.AllocatedSize,
                    node.FileCount))
                .ToArray());
    }
}
