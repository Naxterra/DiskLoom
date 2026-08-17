using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DiskLoom.Core.Models;

public sealed class ScanNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; internal set; }
    public long AllocatedSize { get; internal set; }
    public long FileCount { get; internal set; }
    public long FolderCount { get; internal set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset LastWriteUtc { get; init; }
    public DateTimeOffset LastAccessUtc { get; init; }
    public FileAttributes Attributes { get; init; }
    public uint VolumeSerialNumber { get; internal set; }
    public ulong FileId { get; internal set; }
    public uint HardLinkCount { get; internal set; } = 1;
    public bool IsAdditionalHardLink { get; internal set; }
    public List<ScanNode> Children { get; init; } = [];

    [JsonIgnore]
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name).ToLowerInvariant();

    [JsonIgnore]
    public double CompressionRatio => Size == 0 ? 1 : (double)AllocatedSize / Size;

    public IEnumerable<ScanNode> DescendantsAndSelf()
    {
        var stack = new Stack<ScanNode>();
        stack.Push(this);
        while (stack.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                stack.Push(node.Children[index]);
            }
        }
    }

    public IEnumerable<ScanNode> Files() => DescendantsAndSelf().Where(static node => !node.IsDirectory);
}

public sealed record ScanOptions
{
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 32);
    public bool CalculateAllocatedSize { get; init; } = true;
    public bool PreciseAllocationSize { get; init; }
    public bool DeduplicateHardLinkAllocation { get; init; } = true;
    public bool FollowReparsePoints { get; init; }
    public bool IncludeHidden { get; init; } = true;
    public bool IncludeSystem { get; init; } = true;
    public IReadOnlyList<string> ExcludePatterns { get; init; } =
    [
        "$RECYCLE.BIN",
        "System Volume Information"
    ];
}

public sealed record ScanProgress(
    string CurrentPath,
    long FilesScanned,
    long DirectoriesScanned,
    long BytesScanned,
    TimeSpan Elapsed);

public sealed record ScanIssue(string Path, string Message, string ErrorType);

public sealed record ExtensionStatistic(string Extension, long Size, long AllocatedSize, long FileCount)
{
    public double Percentage { get; internal set; }
}

public sealed record AgeStatistic(string Label, long Size, long FileCount);

public sealed record ScanResult(
    ScanNode Root,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<ScanIssue> Issues,
    IReadOnlyList<ExtensionStatistic> Extensions,
    IReadOnlyList<AgeStatistic> Ages)
{
    public TimeSpan Duration => CompletedUtc - StartedUtc;
}

public sealed record DuplicateFile(string Path, long Size, DateTimeOffset LastWriteUtc);

public sealed record DuplicateGroup(string Sha256, long FileSize, IReadOnlyList<DuplicateFile> Files)
{
    public long ReclaimableSize => Math.Max(0, Files.Count - 1L) * FileSize;
}

public sealed record DuplicateProgress(string Phase, long Processed, long Total, string CurrentPath);

public sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<ScanIssue> Issues)
{
    public long ReclaimableSize => Groups.Sum(static group => group.ReclaimableSize);
}

public sealed record SnapshotEntry(string RelativePath, bool IsDirectory, long Size, long AllocatedSize, long FileCount);

public sealed record ScanSnapshot(string RootPath, DateTimeOffset CreatedUtc, IReadOnlyList<SnapshotEntry> Entries);

public sealed record SnapshotChange(
    string RelativePath,
    long OldSize,
    long NewSize,
    long OldAllocatedSize,
    long NewAllocatedSize,
    ChangeKind Kind)
{
    public long SizeDelta => NewSize - OldSize;
    public long AllocatedDelta => NewAllocatedSize - OldAllocatedSize;
}

public enum ChangeKind
{
    Added,
    Removed,
    Changed,
    Unchanged
}

public sealed record DriveSummary(
    string Name,
    string DisplayName,
    string Format,
    DriveType Type,
    long TotalBytes,
    long FreeBytes,
    bool IsReady)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercentage => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes * 100;
}

public sealed record StorageInsight(
    string Kind,
    string Title,
    string Description,
    string Path,
    long PotentialBytes,
    InsightSeverity Severity,
    long ItemCount = 0,
    DateTimeOffset? ReferenceDate = null);

public enum InsightSeverity
{
    Info,
    Opportunity,
    Attention
}

public sealed record UpdateConfiguration
{
    public string GitHubRepository { get; init; } = string.Empty;
    public string ManifestUrl { get; init; } = string.Empty;
    public string PublisherCertificateSha256 { get; init; } = string.Empty;
    public bool CheckOnStartup { get; init; } = true;
    public int CheckIntervalHours { get; init; } = 24;
}

public sealed record UpdateManifest
{
    public required string Version { get; init; }
    public required string InstallerUrl { get; init; }
    public required string Sha256 { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string ReleaseNotesUrl { get; init; } = string.Empty;
    public DateTimeOffset PublishedUtc { get; init; }
    public bool Mandatory { get; init; }

    [JsonIgnore]
    public bool HasVerifiableInstaller =>
        Uri.TryCreate(InstallerUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        Sha256.Length == 64 &&
        Sha256.All(Uri.IsHexDigit);
}

public sealed record UpdateCheckResult(bool IsConfigured, bool IsUpdateAvailable, Version CurrentVersion, UpdateManifest? Manifest, string Message);
