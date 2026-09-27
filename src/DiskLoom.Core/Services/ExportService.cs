using System.Text;
using System.Text.Json;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class ExportService
{
    public Task ExportCsvAsync(ScanResult result, string destinationPath, CancellationToken cancellationToken = default) =>
        ExportCsvAsync(result, destinationPath, new ExportOptions(), cancellationToken);

    public async Task<ExportSummary> ExportCsvAsync(ScanResult result, string destinationPath, ExportOptions options, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Path,Kind,Size,AllocatedSize,Files,Folders,CreatedUtc,ModifiedUtc,AccessedUtc,Attributes,VolumeSerial,FileId,HardLinkCount,AdditionalHardLink").ConfigureAwait(false);
        var written = 0L;
        foreach (var row in ReportExportService.EnumerateRows(result.Root, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (written >= options.MaximumRows)
            {
                return new ExportSummary(written, IsTruncated: true);
            }
            var node = row.Node;
            var fields = new[]
            {
                node.FullPath,
                node.IsDirectory ? "Folder" : "File",
                node.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.AllocatedSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.FolderCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.CreatedUtc.ToString("O"),
                node.LastWriteUtc.ToString("O"),
                node.LastAccessUtc.ToString("O"),
                node.Attributes.ToString(),
                node.VolumeSerialNumber.ToString("X8"),
                node.FileId.ToString("X16"),
                node.HardLinkCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.IsAdditionalHardLink.ToString()
            };
            await writer.WriteLineAsync(string.Join(',', fields.Select(Escape))).ConfigureAwait(false);
            written++;
        }
        return new ExportSummary(written, IsTruncated: false);
    }

    public async Task ExportJsonAsync(ScanResult result, string destinationPath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // Each folder level nests a node object inside a Children array; the default
            // limit of 64 fails for trees only ~31 folders deep.
            MaxDepth = Math.Max(64, GetTreeDepth(result.Root) * 2 + 8)
        }, cancellationToken).ConfigureAwait(false);
    }

    private static int GetTreeDepth(ScanNode root)
    {
        var maxDepth = 0;
        var stack = new Stack<(ScanNode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var item))
        {
            maxDepth = Math.Max(maxDepth, item.Depth);
            foreach (var child in item.Node.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push((child, item.Depth + 1));
                }
            }
        }
        return maxDepth + 1;
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
