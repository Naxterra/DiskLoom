using System.ComponentModel;
using System.Runtime.CompilerServices;
using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using DiskLoom.Services;
using Microsoft.UI.Xaml;

namespace DiskLoom;

public sealed record BreadcrumbSegment(string Label, string FullPath, ScanNode? Source)
{
    public override string ToString() => Label;
}

public sealed class FolderTreeRow
{
    public FolderTreeRow(string fullPath, ScanNode? source, long parentSize = 0)
    {
        Source = source;
        FullPath = fullPath;
        Name = source?.Name ?? FormatName(fullPath);
        ParentSize = parentSize;
    }

    public FolderTreeRow(DriveSummary drive, ScanNode? source)
    {
        Drive = drive;
        Source = source;
        FullPath = drive.Name;
        Name = drive.DisplayName;
    }

    public DriveSummary? Drive { get; }
    public ScanNode? Source { get; }
    // Size of the folder above this one in the scan; 0 when it was not scanned.
    public long ParentSize { get; }
    public string FullPath { get; }
    public string Name { get; }
    public string Glyph => Drive is null ? "\uE8B7" : "\uE7F1";
    public string PrimarySizeText => Source is not null
        ? ByteFormatter.Format(Source.Size)
        : Drive is { IsReady: true }
            ? ByteFormatter.Format(Drive.UsedBytes)
            : Drive is null
                ? "—"
                : LocalizationService.Get("NotReady");

    // Drives show how full they are; scanned folders show their share of the parent folder.
    public double Percent => Drive is { IsReady: true, TotalBytes: > 0 }
        ? Math.Clamp(Drive.UsedPercentage, 0, 100)
        : Source is not null && ParentSize > 0
            ? Math.Clamp(Source.Size * 100d / ParentSize, 0, 100)
            : 0;

    public Visibility BarVisibility => Drive is { IsReady: true, TotalBytes: > 0 } || (Source is not null && ParentSize > 0)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string PercentToolTip => LocalizationService.Format(
        Drive is { IsReady: true, TotalBytes: > 0 } ? "DriveUsedPercent" : "PercentOfParentTip",
        Percent.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));

    private static string FormatName(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}

public sealed class ResultColumnLayout : INotifyPropertyChanged
{
    internal static ResultColumnLayout Fallback { get; } = new();

    private GridLength _nameWidth = new(1.4, GridUnitType.Star);
    private GridLength _sizeWidth = new(1, GridUnitType.Star);
    private GridLength _allocatedWidth = new(1, GridUnitType.Star);
    private GridLength _modifiedWidth = new(1.2, GridUnitType.Star);
    private GridLength _percentWidth = new(1.1, GridUnitType.Star);
    private GridLength _filesWidth = new(0.8, GridUnitType.Star);
    private GridLength _foldersWidth = new(0.8, GridUnitType.Star);
    private GridLength _createdWidth = new(1.2, GridUnitType.Star);
    private GridLength _accessedWidth = new(1.2, GridUnitType.Star);
    private GridLength _typeWidth = new(0.8, GridUnitType.Star);
    private GridLength _attributesWidth = new(0.7, GridUnitType.Star);

    public event PropertyChangedEventHandler? PropertyChanged;

    public GridLength NameWidth
    {
        get => _nameWidth;
        set => Set(ref _nameWidth, value);
    }

    public GridLength SizeWidth
    {
        get => _sizeWidth;
        set => Set(ref _sizeWidth, value);
    }

    public GridLength AllocatedWidth
    {
        get => _allocatedWidth;
        set => Set(ref _allocatedWidth, value);
    }

    public GridLength ModifiedWidth
    {
        get => _modifiedWidth;
        set => Set(ref _modifiedWidth, value);
    }

    public GridLength PercentWidth
    {
        get => _percentWidth;
        set => Set(ref _percentWidth, value);
    }

    public GridLength FilesWidth
    {
        get => _filesWidth;
        set => Set(ref _filesWidth, value);
    }

    public GridLength FoldersWidth
    {
        get => _foldersWidth;
        set => Set(ref _foldersWidth, value);
    }

    public GridLength CreatedWidth
    {
        get => _createdWidth;
        set => Set(ref _createdWidth, value);
    }

    public GridLength AccessedWidth
    {
        get => _accessedWidth;
        set => Set(ref _accessedWidth, value);
    }

    public GridLength TypeWidth
    {
        get => _typeWidth;
        set => Set(ref _typeWidth, value);
    }

    public GridLength AttributesWidth
    {
        get => _attributesWidth;
        set => Set(ref _attributesWidth, value);
    }

    public void Set(ResultColumnKey key, GridLength value)
    {
        switch (key)
        {
            case ResultColumnKey.Name: NameWidth = value; break;
            case ResultColumnKey.Size: SizeWidth = value; break;
            case ResultColumnKey.Allocated: AllocatedWidth = value; break;
            case ResultColumnKey.Modified: ModifiedWidth = value; break;
            case ResultColumnKey.Percent: PercentWidth = value; break;
            case ResultColumnKey.Files: FilesWidth = value; break;
            case ResultColumnKey.Folders: FoldersWidth = value; break;
            case ResultColumnKey.Created: CreatedWidth = value; break;
            case ResultColumnKey.Accessed: AccessedWidth = value; break;
            case ResultColumnKey.Type: TypeWidth = value; break;
            default: AttributesWidth = value; break;
        }
    }

    private void Set(ref GridLength field, GridLength value, [CallerMemberName] string? propertyName = null)
    {
        if (field.Equals(value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// A row that the shared file context menu (open, reveal, properties, delete) can act on.
public interface IFileActionRow
{
    string Path { get; }
    bool CanDelete { get; }
}

// Live rows are updated in place (Update) while a scan runs, so the list keeps its items.
public sealed class NodeRow(
    ScanNode source,
    bool displayAllocated = false,
    ResultColumnLayout? columnLayout = null,
    long parentSize = 0,
    bool isLive = false) : IFileActionRow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ScanNode Source { get; private set; } = source;
    public bool IsLive { get; } = isLive;
    // Live rows are still growing; deleting or moving them would race the running scan.
    public bool CanDelete => !IsLive;
    public long ParentSize { get; private set; } = parentSize;
    public ResultColumnLayout ColumnLayout { get; } = columnLayout ?? ResultColumnLayout.Fallback;
    public string Name => Source.Name;
    public string Path => Source.FullPath;
    public string Glyph => Source.IsDirectory ? "\uE8B7" : "\uE7C3";
    public string Kind => Source.IsDirectory ? LocalizationService.Get("Folder") : (string.IsNullOrEmpty(Source.Extension) ? LocalizationService.Get("File") : Source.Extension);
    public long DisplaySize => displayAllocated ? Source.AllocatedSize : Source.Size;
    public string PrimarySizeText => ByteFormatter.Format(DisplaySize);
    public string SizeText => ByteFormatter.Format(Source.Size);
    public string AllocatedText => ByteFormatter.Format(Source.AllocatedSize);
    public string CountText => Source.IsDirectory ? LocalizationService.Format("FilesCount", Source.FileCount) : string.Empty;
    public string ModifiedText => FormatDate(Source.LastWriteUtc);
    public string CreatedText => FormatDate(Source.CreatedUtc);
    public string AccessedText => FormatDate(Source.LastAccessUtc);
    public string FileCountText => Source.IsDirectory ? Source.FileCount.ToString("N0") : string.Empty;
    public string FolderCountText => Source.IsDirectory ? Source.FolderCount.ToString("N0") : string.Empty;
    public double PercentOfParent => ParentSize <= 0 ? 0 : Math.Clamp(Source.Size * 100d / ParentSize, 0, 100);
    public string PercentText => ParentSize <= 0 ? "—" : $"{PercentOfParent.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} %";
    public string AttributesText => FormatAttributes(Source.Attributes);

    internal void Update(ScanNode source, long parentSize)
    {
        Source = source;
        ParentSize = parentSize;
        // An empty property name tells every binding on the row to refresh.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string FormatDate(DateTimeOffset value) => value == default ? "—" : value.LocalDateTime.ToString("g");

    // Explorer's attribute letters: Read-only, Hidden, System, Archive, Compressed, Encrypted, Offline, and L for links.
    private static string FormatAttributes(FileAttributes attributes)
    {
        var letters = new System.Text.StringBuilder(8);
        if ((attributes & FileAttributes.ReadOnly) != 0) letters.Append('R');
        if ((attributes & FileAttributes.Hidden) != 0) letters.Append('H');
        if ((attributes & FileAttributes.System) != 0) letters.Append('S');
        if ((attributes & FileAttributes.Archive) != 0) letters.Append('A');
        if ((attributes & FileAttributes.Compressed) != 0) letters.Append('C');
        if ((attributes & FileAttributes.Encrypted) != 0) letters.Append('E');
        if ((attributes & FileAttributes.Offline) != 0) letters.Append('O');
        if ((attributes & FileAttributes.ReparsePoint) != 0) letters.Append('L');
        return letters.Length == 0 ? "—" : letters.ToString();
    }
}

public sealed class ExtensionRow(ExtensionStatistic statistic)
{
    public string Extension { get; } = statistic.Extension == "(no extension)" ? LocalizationService.Get("NoExtension") : statistic.Extension;
    public string SizeText { get; } = ByteFormatter.Format(statistic.Size);
    public string AllocatedText { get; } = ByteFormatter.Format(statistic.AllocatedSize);
    public string CountText { get; } = statistic.FileCount.ToString("N0");
    public string PercentageText { get; } = $"{statistic.Percentage:0.#}%";
    public double Percentage { get; } = statistic.Percentage;
}

public sealed class AgeRow(AgeStatistic statistic)
{
    public string Label { get; } = LocalizeAge(statistic.Label);
    public string SizeText { get; } = ByteFormatter.Format(statistic.Size);
    public string CountText { get; } = LocalizationService.Format("FilesCount", statistic.FileCount);

    internal static string LocalizeAge(string label) => label switch
    {
        "Today" => LocalizationService.Get("AgeToday"),
        "2–7 days" => LocalizationService.Get("Age2To7"),
        "8–30 days" => LocalizationService.Get("Age8To30"),
        "1–6 months" => LocalizationService.Get("Age1To6Months"),
        "6–12 months" => LocalizationService.Get("Age6To12Months"),
        _ => LocalizationService.Get("AgeOlderYear")
    };
}

public sealed class DuplicateRow(int groupNumber, DuplicateGroup group, DuplicateFile file) : IFileActionRow
{
    public int GroupNumber { get; } = groupNumber;
    public bool CanDelete => true;
    public DuplicateGroup Group { get; } = group;
    public DuplicateFile File { get; } = file;
    public string GroupText => LocalizationService.Format("GroupNumber", GroupNumber);
    public string Path => File.Path;
    public string SizeText => ByteFormatter.Format(File.Size);
    public string ReclaimableText => ByteFormatter.Format(Group.ReclaimableSize);
    public string ModifiedText => File.LastWriteUtc.LocalDateTime.ToString("g");
}

public sealed class InsightRow(StorageInsight insight) : IFileActionRow
{
    public StorageInsight Insight { get; } = insight;
    // Aggregate insights (temporary/empty files) point at the scan root, which must never be deleted from here.
    public bool CanDelete => Insight.Kind is "LargeFile" or "StaleFile";
    public string Title => LocalizationService.Get(Insight.Kind switch
    {
        "LargeFile" => "InsightLargeTitle",
        "StaleFile" => "InsightStaleTitle",
        "TemporaryFiles" => "InsightTemporaryTitle",
        _ => "InsightEmptyTitle"
    });
    public string Description => Insight.Kind switch
    {
        "LargeFile" => LocalizationService.Format("InsightLargeDescription", ByteFormatter.Format(Insight.PotentialBytes)),
        "StaleFile" => LocalizationService.Format("InsightStaleDescription", Insight.ReferenceDate?.LocalDateTime.ToString("d") ?? "—"),
        "TemporaryFiles" => LocalizationService.Format("InsightTemporaryDescription", Insight.ItemCount),
        _ => LocalizationService.Format("InsightEmptyDescription", Insight.ItemCount)
    };
    public string Path => Insight.Path;
    public string PotentialText => Insight.PotentialBytes == 0 ? LocalizationService.Get("Review") : ByteFormatter.Format(Insight.PotentialBytes);
    public string Glyph => Insight.Kind switch
    {
        "LargeFile" => "\uE7F1",
        "StaleFile" => "\uE823",
        "TemporaryFiles" => "\uE74D",
        _ => "\uE946"
    };
}

public sealed class ChangeRow(SnapshotChange change)
{
    public SnapshotChange Change { get; } = change;
    public string Kind => LocalizationService.Get(Change.Kind switch
    {
        ChangeKind.Added => "ChangeAdded",
        ChangeKind.Removed => "ChangeRemoved",
        ChangeKind.Changed => "ChangeChanged",
        _ => "ChangeUnchanged"
    });
    public string Path => Change.RelativePath;
    public string OldSize => ByteFormatter.Format(Change.OldSize);
    public string NewSize => ByteFormatter.Format(Change.NewSize);
    public string Delta => $"{(Change.SizeDelta >= 0 ? "+" : "−")}{ByteFormatter.Format(Math.Abs(Change.SizeDelta))}";
}

public sealed class IssueRow(ScanIssue issue)
{
    public string Path { get; } = issue.Path;
    public string Message { get; } = issue.Message;
    public string Type { get; } = issue.ErrorType;
}

public sealed class DriveRow
{
    public DriveRow(DriveSummary drive) => Drive = drive;
    public DriveSummary Drive { get; }
    public string DisplayName => Drive.DisplayName;
    public string FreeText => Drive.IsReady
        ? LocalizationService.Format("DriveFree", ByteFormatter.Format(Drive.FreeBytes))
        : LocalizationService.Get("NotReady");
    public string Detail => Drive.IsReady
        ? LocalizationService.Format("DriveFreeOf", ByteFormatter.Format(Drive.FreeBytes), ByteFormatter.Format(Drive.TotalBytes), Drive.Format)
        : LocalizationService.Get("NotReady");
}

public sealed class ChartSliceRow(string label, long size, long fileCount, double percent, Microsoft.UI.Xaml.Media.SolidColorBrush brush, ScanNode? node)
{
    public string Label { get; } = label;
    public long Size { get; } = size;
    public double Percent { get; } = percent;
    public ScanNode? Node { get; } = node;
    public Microsoft.UI.Xaml.Media.SolidColorBrush Brush { get; } = brush;
    public string SizeText { get; } = ByteFormatter.Format(size);
    public string PercentText { get; } = $"{percent.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} %";
    public string CountText { get; } = LocalizationService.Format("FilesCount", fileCount);
    public string ToolTip => $"{Label}\n{SizeText} · {PercentText} · {CountText}";
}

// One entry in the column chooser; Name is always shown.
public sealed class ColumnChoice(ResultColumnKey key, string title, bool isVisible) : INotifyPropertyChanged
{
    private bool _isVisible = isVisible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ResultColumnKey Key { get; } = key;
    public string Title { get; } = title;
    public bool CanHide => Key != ResultColumnKey.Name;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value || (!CanHide && !value))
            {
                return;
            }
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }
}
