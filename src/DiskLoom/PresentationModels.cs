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
    public FolderTreeRow(string fullPath, ScanNode? source)
    {
        Source = source;
        FullPath = fullPath;
        Name = source?.Name ?? FormatName(fullPath);
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

public sealed class NodeRow(ScanNode source, bool displayAllocated = false, ResultColumnLayout? columnLayout = null)
{
    public ScanNode Source { get; } = source;
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
    public string ModifiedText => Source.LastWriteUtc == default ? "—" : Source.LastWriteUtc.LocalDateTime.ToString("g");
    public string AttributesText => Source.Attributes.ToString();
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
    public string Label { get; } = statistic.Label switch
    {
        "Today" => LocalizationService.Get("AgeToday"),
        "2–7 days" => LocalizationService.Get("Age2To7"),
        "8–30 days" => LocalizationService.Get("Age8To30"),
        "1–6 months" => LocalizationService.Get("Age1To6Months"),
        "6–12 months" => LocalizationService.Get("Age6To12Months"),
        _ => LocalizationService.Get("AgeOlderYear")
    };
    public string SizeText { get; } = ByteFormatter.Format(statistic.Size);
    public string CountText { get; } = LocalizationService.Format("FilesCount", statistic.FileCount);
}

public sealed class DuplicateRow(int groupNumber, DuplicateGroup group, DuplicateFile file)
{
    public int GroupNumber { get; } = groupNumber;
    public DuplicateGroup Group { get; } = group;
    public DuplicateFile File { get; } = file;
    public string GroupText => LocalizationService.Format("GroupNumber", GroupNumber);
    public string Path => File.Path;
    public string SizeText => ByteFormatter.Format(File.Size);
    public string ReclaimableText => ByteFormatter.Format(Group.ReclaimableSize);
    public string ModifiedText => File.LastWriteUtc.LocalDateTime.ToString("g");
}

public sealed class InsightRow(StorageInsight insight)
{
    public StorageInsight Insight { get; } = insight;
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
    public string Detail => Drive.IsReady
        ? LocalizationService.Format("DriveFreeOf", ByteFormatter.Format(Drive.FreeBytes), ByteFormatter.Format(Drive.TotalBytes), Drive.Format)
        : LocalizationService.Get("NotReady");
}
