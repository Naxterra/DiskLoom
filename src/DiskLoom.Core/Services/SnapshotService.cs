using System.IO.Compression;
using System.Text.Json;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class SnapshotService
{
    private const long MaxSnapshotBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task SaveAsync(ScanResult result, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var snapshot = Create(result);

        // Saving over an older snapshot must not destroy it if the write fails or is canceled.
        await AtomicFile.WriteAsync(destinationPath, async file =>
        {
            await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: true);
            await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanSnapshot> LoadAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await using var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var bounded = new SnapshotInput(gzip);
        var snapshot = await JsonSerializer.DeserializeAsync<ScanSnapshot>(bounded, JsonOptions, cancellationToken).ConfigureAwait(false)
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
        var comparer = HasCaseCollisions(older.Entries) || HasCaseCollisions(newer.Entries)
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var before = Index(older.Entries, comparer);
        var after = Index(newer.Entries, comparer);
        var allPaths = before.Keys.Concat(after.Keys).Distinct(comparer);
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
            .ThenBy(static change => change.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasCaseCollisions(IReadOnlyList<SnapshotEntry> entries)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return entries.Any(entry => !paths.Add(entry.RelativePath));
    }

    private static Dictionary<string, SnapshotEntry> Index(IReadOnlyList<SnapshotEntry> entries, StringComparer comparer)
    {
        var result = new Dictionary<string, SnapshotEntry>(entries.Count, comparer);
        foreach (var entry in entries)
            if (!result.TryAdd(entry.RelativePath, entry)) throw new InvalidDataException("The snapshot contains duplicate paths.");
        return result;
    }

    private sealed class SnapshotInput(Stream input) : Stream
    {
        private long _remaining = MaxSnapshotBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            Consume(input.Read(buffer, offset, (int)Math.Min(count, _remaining + 1)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Consume(await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining + 1)], cancellationToken).ConfigureAwait(false));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Consume(int bytes)
        {
            _remaining -= bytes;
            if (_remaining < 0) throw new InvalidDataException("The expanded snapshot exceeds the supported size.");
            return bytes;
        }
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
