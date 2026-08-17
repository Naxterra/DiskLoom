using System.Text;
using System.Text.Json;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class ExportService
{
    public async Task ExportCsvAsync(ScanResult result, string destinationPath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Path,Kind,Size,AllocatedSize,Files,Folders,CreatedUtc,ModifiedUtc,AccessedUtc,Attributes,VolumeSerial,FileId,HardLinkCount,AdditionalHardLink").ConfigureAwait(false);
        foreach (var node in result.Root.DescendantsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        }
    }

    public async Task ExportJsonAsync(ScanResult result, string destinationPath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
