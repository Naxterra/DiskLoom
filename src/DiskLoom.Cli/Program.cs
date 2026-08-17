using System.Text;
using DiskLoom.Core.Models;
using DiskLoom.Core.Services;

return await DiskLoomCli.RunAsync(args);

internal static class DiskLoomCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(static arg => arg is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = Parse(args);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var scanner = new FileSystemScanner();
            var lastProgress = DateTimeOffset.MinValue;
            var progress = new Progress<ScanProgress>(value =>
            {
                if (DateTimeOffset.UtcNow - lastProgress < TimeSpan.FromMilliseconds(250))
                {
                    return;
                }
                lastProgress = DateTimeOffset.UtcNow;
                Console.Write($"\rScanning {value.FilesScanned,12:N0} files  {ByteFormatter.Format(value.BytesScanned),12}  {value.CurrentPath.Truncate(70),-70}");
            });

            var result = await scanner.ScanAsync(options.ScanPath, new ScanOptions
            {
                Parallelism = options.Parallelism,
                CalculateAllocatedSize = true,
                FollowReparsePoints = options.FollowLinks,
                ExcludePatterns = options.Excludes.Count == 0 ? new ScanOptions().ExcludePatterns : options.Excludes
            }, progress, cancellation.Token);

            Console.WriteLine();
            Console.WriteLine($"Scanned {result.Root.FileCount:N0} files in {result.Root.FolderCount + 1:N0} folders.");
            Console.WriteLine($"Logical: {ByteFormatter.Format(result.Root.Size)}  Allocated: {ByteFormatter.Format(result.Root.AllocatedSize)}  Issues: {result.Issues.Count:N0}");

            var exporter = new ExportService();
            if (!string.IsNullOrWhiteSpace(options.ExportPath))
            {
                if (Path.GetExtension(options.ExportPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    await exporter.ExportJsonAsync(result, options.ExportPath, cancellation.Token);
                }
                else
                {
                    await exporter.ExportCsvAsync(result, options.ExportPath, cancellation.Token);
                }
                Console.WriteLine($"Exported: {Path.GetFullPath(options.ExportPath)}");
            }

            if (!string.IsNullOrWhiteSpace(options.SnapshotPath))
            {
                await new SnapshotService().SaveAsync(result, options.SnapshotPath, cancellation.Token);
                Console.WriteLine($"Snapshot: {Path.GetFullPath(options.SnapshotPath)}");
            }

            if (!string.IsNullOrWhiteSpace(options.DuplicatesPath))
            {
                Console.WriteLine("Verifying duplicates…");
                var duplicates = await new DuplicateFinder().FindAsync(
                    result.Root,
                    options.MinimumDuplicateBytes,
                    cancellationToken: cancellation.Token);
                await ExportDuplicatesAsync(duplicates, options.DuplicatesPath, cancellation.Token);
                Console.WriteLine($"Duplicates: {duplicates.Groups.Count:N0} groups, up to {ByteFormatter.Format(duplicates.ReclaimableSize)} reclaimable");
            }

            return result.Issues.Count == 0 ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation canceled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"DiskLoom: {exception.Message}");
            return 1;
        }
    }

    private static CliOptions Parse(IReadOnlyList<string> args)
    {
        string? scanPath = null;
        string? exportPath = null;
        string? snapshotPath = null;
        string? duplicatesPath = null;
        var excludes = new List<string>();
        var parallelism = Math.Clamp(Environment.ProcessorCount, 2, 32);
        var minimumDuplicateBytes = 1024L * 1024;
        var followLinks = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            string NextValue()
            {
                if (++index >= args.Count)
                {
                    throw new ArgumentException($"Missing value after {argument}.");
                }
                return args[index];
            }

            switch (argument.ToLowerInvariant())
            {
                case "--scan": scanPath = NextValue(); break;
                case "--export": exportPath = NextValue(); break;
                case "--snapshot": snapshotPath = NextValue(); break;
                case "--duplicates": duplicatesPath = NextValue(); break;
                case "--exclude": excludes.Add(NextValue()); break;
                case "--parallel": parallelism = Math.Clamp(int.Parse(NextValue()), 1, 32); break;
                case "--min-duplicate-mb": minimumDuplicateBytes = checked((long)(double.Parse(NextValue(), System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024)); break;
                case "--follow-links": followLinks = true; break;
                default: throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        if (string.IsNullOrWhiteSpace(scanPath))
        {
            throw new ArgumentException("--scan PATH is required.");
        }
        return new CliOptions(scanPath, exportPath, snapshotPath, duplicatesPath, excludes, parallelism, minimumDuplicateBytes, followLinks);
    }

    private static async Task ExportDuplicatesAsync(DuplicateResult result, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("Group,SHA256,Path,Size,ModifiedUtc,GroupReclaimable");
        for (var groupIndex = 0; groupIndex < result.Groups.Count; groupIndex++)
        {
            var group = result.Groups[groupIndex];
            foreach (var file in group.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(string.Join(',', new[]
                {
                    (groupIndex + 1).ToString(),
                    Quote(group.Sha256),
                    Quote(file.Path),
                    file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Quote(file.LastWriteUtc.ToString("O")),
                    group.ReclaimableSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
            }
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            DiskLoom CLI 0.1
            Usage:
              DiskLoom.Cli --scan PATH [options]

            Options:
              --export FILE             Export the complete scan as .csv or .json
              --snapshot FILE           Save a compressed .diskloom snapshot
              --duplicates FILE         Verify duplicates and export them as CSV
              --min-duplicate-mb NUMBER Ignore smaller files (default: 1 MB)
              --exclude PATTERN         Exclude a name/path wildcard; repeat as needed
              --parallel NUMBER         Directory workers, 1–32
              --follow-links            Follow directory reparse points with loop protection
              --help                    Show this help

            Exit codes: 0 success, 1 error, 2 completed with access issues, 130 canceled.
            """);
    }

    private sealed record CliOptions(
        string ScanPath,
        string? ExportPath,
        string? SnapshotPath,
        string? DuplicatesPath,
        IReadOnlyList<string> Excludes,
        int Parallelism,
        long MinimumDuplicateBytes,
        bool FollowLinks);

    private static string Truncate(this string value, int length) => value.Length <= length ? value : "…" + value[^Math.Max(1, length - 1)..];
}
