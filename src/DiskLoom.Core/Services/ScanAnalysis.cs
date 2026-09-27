using System.IO.Enumeration;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public enum FileTypeCategory
{
    Any,
    Video,
    Audio,
    Image,
    Document,
    Archive,
    DiskImage,
    Program,
    Temporary,
    Other
}

public static class FileTypeCategories
{
    private static readonly Dictionary<string, FileTypeCategory> ByExtension = Build();

    // Selectable categories in display order (Other = everything no category claims).
    public static IReadOnlyList<FileTypeCategory> All { get; } =
    [
        FileTypeCategory.Video,
        FileTypeCategory.Audio,
        FileTypeCategory.Image,
        FileTypeCategory.Document,
        FileTypeCategory.Archive,
        FileTypeCategory.DiskImage,
        FileTypeCategory.Program,
        FileTypeCategory.Temporary,
        FileTypeCategory.Other
    ];

    public static FileTypeCategory Classify(string extension) =>
        ByExtension.TryGetValue(extension, out var category) ? category : FileTypeCategory.Other;

    public static bool Matches(FileTypeCategory category, string extension) =>
        category == FileTypeCategory.Any || Classify(extension) == category;

    private static Dictionary<string, FileTypeCategory> Build()
    {
        var map = new Dictionary<string, FileTypeCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileTypeCategory category, string extensions)
        {
            foreach (var extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map[extension] = category;
            }
        }

        Add(FileTypeCategory.Video, ".mp4 .mkv .avi .mov .wmv .m4v .webm .flv .mpg .mpeg .m2ts .mts .3gp .vob .ts .bik .bk2");
        Add(FileTypeCategory.Audio, ".mp3 .flac .wav .aac .m4a .ogg .opus .wma .aiff .aif .ape .mid .midi");
        Add(FileTypeCategory.Image, ".jpg .jpeg .png .gif .bmp .tif .tiff .webp .heic .heif .avif .raw .cr2 .cr3 .nef .arw .orf .rw2 .dng .psd .svg .ico");
        Add(FileTypeCategory.Document, ".pdf .doc .docx .xls .xlsx .xlsm .ppt .pptx .odt .ods .odp .txt .rtf .md .csv .epub .one .xps .pst .ost .eml .msg");
        Add(FileTypeCategory.Archive, ".zip .7z .rar .tar .gz .tgz .bz2 .xz .zst .cab .lz .lzma");
        Add(FileTypeCategory.DiskImage, ".iso .img .vhd .vhdx .vmdk .vdi .qcow2 .wim .esd .dmg .mrimg .mrimgx .tib .tibx .vbk .gho");
        Add(FileTypeCategory.Program, ".exe .msi .msix .msixbundle .appx .appxbundle .dll .sys .bat .cmd .ps1 .jar .apk");
        Add(FileTypeCategory.Temporary, ".tmp .temp .dmp .bak .old .chk .log .etl .crdownload .part");
        return map;
    }
}

public static class ScanStatistics
{
    // Stable keys; the app localizes them.
    public static IReadOnlyList<string> AgeLabels { get; } =
    [
        "Today",
        "2–7 days",
        "8–30 days",
        "1–6 months",
        "6–12 months",
        "Older than a year"
    ];

    public static int GetAgeBucket(TimeSpan age) =>
        age < TimeSpan.FromDays(1) ? 0
        : age < TimeSpan.FromDays(8) ? 1
        : age < TimeSpan.FromDays(31) ? 2
        : age < TimeSpan.FromDays(183) ? 3
        : age < TimeSpan.FromDays(366) ? 4
        : 5;

    public static (IReadOnlyList<ExtensionStatistic> Extensions, IReadOnlyList<AgeStatistic> Ages) Build(
        ScanNode root,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var extensions = new Dictionary<string, ExtensionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var ageSizes = new long[AgeLabels.Count];
        var ageCounts = new long[AgeLabels.Count];
        var visited = 0;

        foreach (var file in root.Files())
        {
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var extension = string.IsNullOrEmpty(file.Extension) ? "(no extension)" : file.Extension;
            if (!extensions.TryGetValue(extension, out var aggregate))
            {
                aggregate = new ExtensionAccumulator();
                extensions.Add(extension, aggregate);
            }
            aggregate.Size += file.Size;
            aggregate.AllocatedSize += file.AllocatedSize;
            aggregate.FileCount++;

            var ageIndex = GetAgeBucket(now - file.LastWriteUtc);
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
        var ageStatistics = AgeLabels
            .Select((label, index) => new AgeStatistic(label, ageSizes[index], ageCounts[index]))
            .ToArray();
        return (extensionStatistics, ageStatistics);
    }

    private sealed class ExtensionAccumulator
    {
        public long Size;
        public long AllocatedSize;
        public long FileCount;
    }
}

// Semicolon-separated wildcard patterns; a pattern without * or ? matches as a substring.
internal sealed class NamePatternMatcher
{
    private readonly string[] _patterns;

    public NamePatternMatcher(string? patterns)
    {
        _patterns = (patterns ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static pattern => pattern.IndexOfAny(['*', '?']) >= 0 ? pattern : $"*{pattern}*")
            .ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    public bool Matches(string name)
    {
        foreach (var pattern in _patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }
        return _patterns.Length == 0;
    }
}

public enum SearchItemKind
{
    Files,
    Folders,
    FilesAndFolders
}

public sealed record SearchCriteria
{
    public string NamePattern { get; init; } = string.Empty;
    public FileTypeCategory Category { get; init; } = FileTypeCategory.Any;
    public SearchItemKind Kind { get; init; } = SearchItemKind.Files;
    public long? MinimumSize { get; init; }
    public long? MaximumSize { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }
    public int MaximumResults { get; init; } = 10_000;
}

public sealed record SearchResult(IReadOnlyList<ScanNode> Matches, long MatchCount, long MatchedSize)
{
    public bool IsTruncated => MatchCount > Matches.Count;
}

public static class ScanSearch
{
    // Returns the largest matches (up to MaximumResults) plus the count and size of all of them.
    public static SearchResult Search(ScanNode root, SearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var matcher = new NamePatternMatcher(criteria.NamePattern);
        var limit = Math.Max(1, criteria.MaximumResults);
        var largest = new PriorityQueue<ScanNode, long>(limit + 1);
        var count = 0L;
        var size = 0L;
        var visited = 0;

        foreach (var node in root.DescendantsAndSelf())
        {
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ReferenceEquals(node, root) || !IsMatch(node, criteria, matcher))
            {
                continue;
            }

            count++;
            size += node.Size;
            largest.Enqueue(node, node.Size);
            if (largest.Count > limit)
            {
                largest.Dequeue();
            }
        }

        var matches = new ScanNode[largest.Count];
        for (var index = matches.Length - 1; index >= 0; index--)
        {
            matches[index] = largest.Dequeue();
        }
        return new SearchResult(matches, count, size);
    }

    private static bool IsMatch(ScanNode node, SearchCriteria criteria, NamePatternMatcher matcher)
    {
        if (node.IsDirectory ? criteria.Kind == SearchItemKind.Files : criteria.Kind == SearchItemKind.Folders)
        {
            return false;
        }
        // Types describe file contents; a folder never matches a specific category.
        if (criteria.Category != FileTypeCategory.Any &&
            (node.IsDirectory || !FileTypeCategories.Matches(criteria.Category, node.Extension)))
        {
            return false;
        }
        if (node.Size < (criteria.MinimumSize ?? long.MinValue) || node.Size > (criteria.MaximumSize ?? long.MaxValue))
        {
            return false;
        }
        if ((criteria.ModifiedAfter is { } after && node.LastWriteUtc < after) ||
            (criteria.ModifiedBefore is { } before && node.LastWriteUtc > before))
        {
            return false;
        }
        return matcher.Matches(node.Name);
    }
}

public sealed record TreeFilter
{
    public FileTypeCategory Category { get; init; } = FileTypeCategory.Any;
    public string NamePattern { get; init; } = string.Empty;
    // Keep files last changed at least this many days ago.
    public int? OlderThanDays { get; init; }
    // Keep files last changed within this many days.
    public int? NewerThanDays { get; init; }

    public bool IsEmpty =>
        Category == FileTypeCategory.Any &&
        string.IsNullOrWhiteSpace(NamePattern) &&
        OlderThanDays is null &&
        NewerThanDays is null;
}

public static class ScanTreeFilter
{
    // Builds a copy of the tree that holds only matching files; folder sizes, counts and
    // statistics count those files alone, and folders without any are left out.
    public static ScanResult Apply(ScanResult source, TreeFilter filter, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (filter.IsEmpty)
        {
            return source;
        }

        var matcher = new NamePatternMatcher(filter.NamePattern);
        bool Matches(ScanNode file)
        {
            if (!FileTypeCategories.Matches(filter.Category, file.Extension))
            {
                return false;
            }
            var age = now - file.LastWriteUtc;
            if ((filter.OlderThanDays is { } older && age < TimeSpan.FromDays(older)) ||
                (filter.NewerThanDays is { } newer && age > TimeSpan.FromDays(newer)))
            {
                return false;
            }
            return matcher.Matches(file.Name);
        }

        var rootClone = CloneDirectory(source.Root);
        var stack = new Stack<Frame>();
        stack.Push(new Frame(source.Root, rootClone));
        var visited = 0;
        while (stack.TryPeek(out var frame))
        {
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (frame.NextChild < frame.Source.Children.Count)
            {
                var child = frame.Source.Children[frame.NextChild++];
                if (child.IsDirectory)
                {
                    stack.Push(new Frame(child, CloneDirectory(child)));
                }
                else if (Matches(child))
                {
                    frame.Clone.Children.Add(CloneFile(child));
                    frame.Clone.Size += child.Size;
                    frame.Clone.AllocatedSize += child.AllocatedSize;
                    frame.Clone.FileCount++;
                }
                continue;
            }

            stack.Pop();
            SortBySize(frame.Clone);
            if (stack.TryPeek(out var parent) && frame.Clone.FileCount > 0)
            {
                parent.Clone.Children.Add(frame.Clone);
                parent.Clone.Size += frame.Clone.Size;
                parent.Clone.AllocatedSize += frame.Clone.AllocatedSize;
                parent.Clone.FileCount += frame.Clone.FileCount;
                parent.Clone.FolderCount += frame.Clone.FolderCount + 1;
            }
        }

        var (extensions, ages) = ScanStatistics.Build(rootClone, now, cancellationToken);
        return source with { Root = rootClone, Extensions = extensions, Ages = ages };
    }

    private static ScanNode CloneDirectory(ScanNode node) => new()
    {
        Name = node.Name,
        FullPath = node.FullPath,
        IsDirectory = true,
        CreatedUtc = node.CreatedUtc,
        LastWriteUtc = node.LastWriteUtc,
        LastAccessUtc = node.LastAccessUtc,
        Attributes = node.Attributes,
        VolumeSerialNumber = node.VolumeSerialNumber,
        FileId = node.FileId
    };

    private static ScanNode CloneFile(ScanNode node) => new()
    {
        Name = node.Name,
        FullPath = node.FullPath,
        IsDirectory = false,
        Size = node.Size,
        AllocatedSize = node.AllocatedSize,
        FileCount = 1,
        CreatedUtc = node.CreatedUtc,
        LastWriteUtc = node.LastWriteUtc,
        LastAccessUtc = node.LastAccessUtc,
        Attributes = node.Attributes,
        VolumeSerialNumber = node.VolumeSerialNumber,
        FileId = node.FileId,
        HardLinkCount = node.HardLinkCount,
        IsAdditionalHardLink = node.IsAdditionalHardLink
    };

    private static void SortBySize(ScanNode node) => node.Children.Sort(static (left, right) =>
    {
        var size = right.Size.CompareTo(left.Size);
        return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    });

    private sealed class Frame(ScanNode source, ScanNode clone)
    {
        public ScanNode Source { get; } = source;
        public ScanNode Clone { get; } = clone;
        public int NextChild;
    }
}

public enum BreakdownKind
{
    Children,
    Extensions,
    Categories,
    Ages
}

// Label is a file/folder name, an extension, a FileTypeCategory name or an age key (see ScanStatistics.AgeLabels).
public sealed record BreakdownSlice(string Label, long Size, long FileCount, ScanNode? Node = null, bool IsOther = false);

public static class FolderBreakdown
{
    public static IReadOnlyList<BreakdownSlice> Create(
        ScanNode folder,
        BreakdownKind kind,
        int maximumSlices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) => kind switch
    {
        BreakdownKind.Children => Collapse(
            folder.Children.Where(static child => child.Size > 0)
                .Select(static child => new BreakdownSlice(child.Name, child.Size, child.FileCount, child)),
            maximumSlices),
        BreakdownKind.Extensions => Collapse(
            ScanStatistics.Build(folder, now, cancellationToken).Extensions
                .Where(static extension => extension.Size > 0)
                .Select(static extension => new BreakdownSlice(extension.Extension, extension.Size, extension.FileCount)),
            maximumSlices),
        BreakdownKind.Categories => ByCategory(folder, cancellationToken),
        _ => ScanStatistics.Build(folder, now, cancellationToken).Ages
            .Select(static age => new BreakdownSlice(age.Label, age.Size, age.FileCount))
            .ToArray()
    };

    private static IReadOnlyList<BreakdownSlice> ByCategory(ScanNode folder, CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<FileTypeCategory, (long Size, long Count)>();
        var visited = 0;
        foreach (var file in folder.Files())
        {
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var category = FileTypeCategories.Classify(file.Extension);
            sizes.TryGetValue(category, out var total);
            sizes[category] = (total.Size + file.Size, total.Count + 1);
        }
        return sizes
            .Where(static pair => pair.Value.Size > 0)
            .OrderByDescending(static pair => pair.Value.Size)
            .Select(static pair => new BreakdownSlice(pair.Key.ToString(), pair.Value.Size, pair.Value.Count))
            .ToArray();
    }

    // Keeps the largest slices and folds the rest into one "other" slice.
    private static IReadOnlyList<BreakdownSlice> Collapse(IEnumerable<BreakdownSlice> slices, int maximumSlices)
    {
        var ordered = slices.OrderByDescending(static slice => slice.Size).ToArray();
        var keep = Math.Max(1, maximumSlices);
        if (ordered.Length <= keep)
        {
            return ordered;
        }

        var rest = ordered.Skip(keep - 1).ToArray();
        return ordered
            .Take(keep - 1)
            .Append(new BreakdownSlice(string.Empty, rest.Sum(static slice => slice.Size), rest.Sum(static slice => slice.FileCount), IsOther: true))
            .ToArray();
    }
}
