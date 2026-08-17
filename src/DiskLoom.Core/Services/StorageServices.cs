using System.Diagnostics;
using DiskLoom.Core.Models;
using Microsoft.VisualBasic.FileIO;

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

public sealed class FileOperationService
{
    public Task RecycleAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else
        {
            throw new FileNotFoundException("The selected item no longer exists.", path);
        }
    }, cancellationToken);

    public Task DeletePermanentlyAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            throw new FileNotFoundException("The selected item no longer exists.", path);
        }
    }, cancellationToken);

    public void ShowInExplorer(string path)
    {
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
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
