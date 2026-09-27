using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed record ExportOptions
{
    // Folder levels below the exported root to include; null = all. Level 0 is the root.
    public int? MaximumDepth { get; init; }
    public bool IncludeFiles { get; init; } = true;
    public int MaximumRows { get; init; } = int.MaxValue;
}

public sealed record ExportSummary(long RowsWritten, bool IsTruncated);

// Column titles and captions for reports; the app passes localized text.
public sealed record ReportText
{
    public string Title { get; init; } = "DiskLoom report";
    public string ScannedFolder { get; init; } = "Scanned folder";
    public string ScanDate { get; init; } = "Scanned";
    public string Generated { get; init; } = "Generated";
    public string Path { get; init; } = "Path";
    public string Name { get; init; } = "Name";
    public string Kind { get; init; } = "Type";
    public string Folder { get; init; } = "Folder";
    public string File { get; init; } = "File";
    public string Level { get; init; } = "Level";
    public string Size { get; init; } = "Size";
    public string SizeOnDisk { get; init; } = "Size on disk";
    public string SizeBytes { get; init; } = "Size (bytes)";
    public string AllocatedBytes { get; init; } = "Size on disk (bytes)";
    public string SizeMegabytes { get; init; } = "Size (MB)";
    public string PercentOfParent { get; init; } = "% of parent";
    public string Files { get; init; } = "Files";
    public string Folders { get; init; } = "Folders";
    public string Modified { get; init; } = "Modified";
    public string Created { get; init; } = "Created";
    public string Accessed { get; init; } = "Last accessed";
    public string Attributes { get; init; } = "Attributes";
    public string FileTypes { get; init; } = "File types";
    public string Extension { get; init; } = "Extension";
    public string Share { get; init; } = "Share";
    public string NoExtension { get; init; } = "(no extension)";
    public string Truncated { get; init; } = "The report was shortened to {0:N0} rows. Reduce the depth or leave out files to include everything.";
    public string DepthAll { get; init; } = "All levels";
    public string Depth { get; init; } = "Depth";
}

public sealed record ExportRow(ScanNode Node, int Level, long ParentSize)
{
    public double PercentOfParent => ParentSize <= 0 ? 100 : Node.Size * 100d / ParentSize;
}

public sealed class ReportExportService
{
    // Pre-order rows (a folder before its contents), largest first, honouring depth and file options.
    public static IEnumerable<ExportRow> EnumerateRows(ScanNode root, ExportOptions options)
    {
        var stack = new Stack<ExportRow>();
        stack.Push(new ExportRow(root, 0, 0));
        while (stack.TryPop(out var row))
        {
            yield return row;
            if (!row.Node.IsDirectory || row.Level >= (options.MaximumDepth ?? int.MaxValue))
            {
                continue;
            }

            for (var index = row.Node.Children.Count - 1; index >= 0; index--)
            {
                var child = row.Node.Children[index];
                if (child.IsDirectory || options.IncludeFiles)
                {
                    stack.Push(new ExportRow(child, row.Level + 1, row.Node.Size));
                }
            }
        }
    }

    public Task<ExportSummary> ExportXlsxAsync(
        ScanResult result,
        string destinationPath,
        ExportOptions options,
        ReportText? text = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        text ??= new ReportText();
        // Excel's hard limit is 1,048,576 rows including the header.
        var limit = Math.Min(options.MaximumRows, 1_048_575);
        using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        WritePart(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """);
        WritePart(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        WritePart(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        var treeSheet = SheetName(result.Root.Name);
        var typesSheet = SheetName(text.FileTypes);
        if (typesSheet.Equals(treeSheet, StringComparison.OrdinalIgnoreCase))
        {
            typesSheet = SheetName($"{typesSheet} 2");
        }
        WritePart(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{XmlAttribute(treeSheet)}" sheetId="1" r:id="rId1"/><sheet name="{XmlAttribute(typesSheet)}" sheetId="2" r:id="rId2"/></sheets></workbook>
            """);
        WritePart(zip, "xl/styles.xml", StylesXml);

        long written;
        bool truncated;
        using (var writer = CreateXmlWriter(zip, "xl/worksheets/sheet1.xml"))
        {
            (written, truncated) = WriteTreeSheet(writer, result.Root, options, text, limit, cancellationToken);
        }
        using (var writer = CreateXmlWriter(zip, "xl/worksheets/sheet2.xml"))
        {
            WriteExtensionSheet(writer, result, text);
        }
        return new ExportSummary(written, truncated);
    }, cancellationToken);

    public async Task<ExportSummary> ExportHtmlAsync(
        ScanResult result,
        string destinationPath,
        ExportOptions options,
        ReportText? text = null,
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default)
    {
        text ??= new ReportText();
        culture ??= CultureInfo.CurrentCulture;
        var (html, summary) = await Task.Run(() => BuildHtml(result, options, text, culture, cancellationToken), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(destinationPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        return summary;
    }

    public static (string Html, ExportSummary Summary) BuildHtml(
        ScanResult result,
        ExportOptions options,
        ReportText text,
        CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        string E(string value) => WebUtility.HtmlEncode(Sanitize(value));
        string Date(DateTimeOffset value) => value == default ? "—" : value.LocalDateTime.ToString("g", culture);
        string Bytes(long value) => ByteFormatter.Format(value);

        var root = result.Root;
        var builder = new StringBuilder(64 * 1024);
        builder.Append($$"""
            <!DOCTYPE html>
            <html lang="{{E(culture.TwoLetterISOLanguageName)}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(text.Title)}} – {{E(root.FullPath)}}</title>
            <style>
            :root { color-scheme: light; }
            body { font: 13px/1.4 "Segoe UI", system-ui, sans-serif; color: #10233e; margin: 24px; background: #fff; }
            h1 { font-size: 22px; margin: 0 0 4px; color: #1268c4; }
            h2 { font-size: 16px; margin: 28px 0 8px; color: #007b62; }
            .meta { color: #50627a; margin-bottom: 16px; }
            .cards { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 8px; }
            .card { border: 1px solid #7dbeff; border-radius: 8px; padding: 8px 14px; background: #f2f7fd; min-width: 140px; }
            .card b { display: block; font-size: 18px; }
            table { border-collapse: collapse; width: 100%; }
            thead { display: table-header-group; }
            th { text-align: left; background: #ddebfa; color: #0b4f96; font-weight: 600; padding: 5px 8px; border-bottom: 1px solid #7dbeff; }
            td { padding: 3px 8px; border-bottom: 1px solid #e3ebf5; vertical-align: middle; }
            tr { break-inside: avoid; }
            td.num, th.num { text-align: right; white-space: nowrap; }
            td.name { word-break: break-all; }
            tr.folder td.name { font-weight: 600; }
            .bar { display: inline-block; width: 70px; height: 7px; background: #e3ebf5; border-radius: 4px; vertical-align: middle; margin-right: 6px; overflow: hidden; }
            .bar i { display: block; height: 100%; background: linear-gradient(90deg, #1268c4, #00a2c7); }
            .note { margin-top: 12px; padding: 8px 12px; background: #fff4e0; border: 1px solid #ffc263; border-radius: 6px; }
            @media print { body { margin: 0; } }
            </style>
            </head>
            <body>
            <h1>{{E(text.Title)}}</h1>
            <div class="meta">{{E(text.ScannedFolder)}}: <b>{{E(root.FullPath)}}</b> · {{E(text.ScanDate)}}: {{E(Date(result.CompletedUtc))}} · {{E(text.Generated)}}: {{E(DateTimeOffset.Now.ToString("g", culture))}} · {{E(text.Depth)}}: {{E(options.MaximumDepth?.ToString(culture) ?? text.DepthAll)}}</div>
            <div class="cards">
            <div class="card">{{E(text.Size)}}<b>{{E(Bytes(root.Size))}}</b></div>
            <div class="card">{{E(text.SizeOnDisk)}}<b>{{E(Bytes(root.AllocatedSize))}}</b></div>
            <div class="card">{{E(text.Files)}}<b>{{E(root.FileCount.ToString("N0", culture))}}</b></div>
            <div class="card">{{E(text.Folders)}}<b>{{E(root.FolderCount.ToString("N0", culture))}}</b></div>
            </div>
            <table>
            <thead><tr><th>{{E(text.Name)}}</th><th class="num">{{E(text.Size)}}</th><th>{{E(text.PercentOfParent)}}</th><th class="num">{{E(text.Files)}}</th><th class="num">{{E(text.Modified)}}</th></tr></thead>
            <tbody>

            """);

        var written = 0L;
        var truncated = false;
        foreach (var row in EnumerateRows(root, options))
        {
            if ((written & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (written >= options.MaximumRows)
            {
                truncated = true;
                break;
            }

            var node = row.Node;
            var percent = Math.Clamp(row.PercentOfParent, 0, 100);
            var name = row.Level == 0 ? node.FullPath : node.Name;
            builder.Append(node.IsDirectory ? "<tr class=\"folder\">" : "<tr>")
                .Append("<td class=\"name\" style=\"padding-left:").Append((8 + row.Level * 16).ToString(CultureInfo.InvariantCulture)).Append("px\">")
                .Append(node.IsDirectory ? "📁 " : string.Empty).Append(E(name)).Append("</td>")
                .Append("<td class=\"num\">").Append(E(Bytes(node.Size))).Append("</td>")
                .Append("<td class=\"num\" style=\"text-align:left\"><span class=\"bar\"><i style=\"width:")
                .Append(percent.ToString("0.#", CultureInfo.InvariantCulture)).Append("%\"></i></span>")
                .Append(E(percent.ToString("0.0", culture))).Append(" %</td>")
                .Append("<td class=\"num\">").Append(node.IsDirectory ? E(node.FileCount.ToString("N0", culture)) : string.Empty).Append("</td>")
                .Append("<td class=\"num\">").Append(E(Date(node.LastWriteUtc))).Append("</td></tr>\n");
            written++;
        }

        builder.Append("</tbody></table>\n");
        if (truncated)
        {
            builder.Append("<div class=\"note\">").Append(E(string.Format(culture, text.Truncated, options.MaximumRows))).Append("</div>\n");
        }

        builder.Append($$"""
            <h2>{{E(text.FileTypes)}}</h2>
            <table>
            <thead><tr><th>{{E(text.Extension)}}</th><th class="num">{{E(text.Size)}}</th><th>{{E(text.Share)}}</th><th class="num">{{E(text.Files)}}</th></tr></thead>
            <tbody>

            """);
        foreach (var extension in result.Extensions.Take(50))
        {
            var share = Math.Clamp(extension.Percentage, 0, 100);
            builder.Append("<tr><td>").Append(E(extension.Extension == "(no extension)" ? text.NoExtension : extension.Extension)).Append("</td>")
                .Append("<td class=\"num\">").Append(E(Bytes(extension.Size))).Append("</td>")
                .Append("<td><span class=\"bar\"><i style=\"width:").Append(share.ToString("0.#", CultureInfo.InvariantCulture)).Append("%\"></i></span>")
                .Append(E(share.ToString("0.0", culture))).Append(" %</td>")
                .Append("<td class=\"num\">").Append(E(extension.FileCount.ToString("N0", culture))).Append("</td></tr>\n");
        }
        builder.Append("</tbody></table>\n</body>\n</html>\n");
        return (builder.ToString(), new ExportSummary(written, truncated));
    }

    private static (long Written, bool Truncated) WriteTreeSheet(
        XmlWriter writer,
        ScanNode root,
        ExportOptions options,
        ReportText text,
        long limit,
        CancellationToken cancellationToken)
    {
        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        WriteColumns(writer, [48, 9, 7, 14, 16, 18, 11, 11, 11, 17, 17, 70]);

        writer.WriteStartElement("sheetData");
        WriteHeaderRow(writer, [text.Name, text.Kind, text.Level, text.SizeMegabytes, text.SizeBytes, text.AllocatedBytes, text.PercentOfParent, text.Files, text.Folders, text.Modified, text.Created, text.Path]);

        var written = 0L;
        var truncated = false;
        foreach (var row in EnumerateRows(root, options))
        {
            if ((written & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (written >= limit)
            {
                truncated = true;
                break;
            }

            var node = row.Node;
            writer.WriteStartElement("row");
            // Indented, bold-for-folders name cells mirror the folder tree (styles 5–20 / 21–36).
            var indentStyle = (node.IsDirectory ? 21 : 5) + Math.Min(row.Level, 15);
            WriteStringCell(writer, row.Level == 0 ? node.FullPath : node.Name, indentStyle);
            WriteStringCell(writer, node.IsDirectory ? text.Folder : text.File);
            WriteNumberCell(writer, row.Level);
            WriteNumberCell(writer, node.Size / 1048576d, style: 2);
            WriteNumberCell(writer, node.Size, style: 3);
            WriteNumberCell(writer, node.AllocatedSize, style: 3);
            WriteNumberCell(writer, row.PercentOfParent / 100d, style: 4);
            WriteNumberCell(writer, node.FileCount, style: 3);
            WriteNumberCell(writer, node.FolderCount, style: 3);
            WriteDateCell(writer, node.LastWriteUtc);
            WriteDateCell(writer, node.CreatedUtc);
            WriteStringCell(writer, node.FullPath);
            writer.WriteEndElement();
            written++;
        }

        writer.WriteEndElement();
        writer.WriteStartElement("autoFilter");
        writer.WriteAttributeString("ref", $"A1:L{written + 1}");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        return (written, truncated);
    }

    private static void WriteExtensionSheet(XmlWriter writer, ScanResult result, ReportText text)
    {
        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        WriteColumns(writer, [20, 14, 16, 18, 11, 12]);
        writer.WriteStartElement("sheetData");
        WriteHeaderRow(writer, [text.Extension, text.SizeMegabytes, text.SizeBytes, text.AllocatedBytes, text.Share, text.Files]);
        foreach (var extension in result.Extensions)
        {
            writer.WriteStartElement("row");
            WriteStringCell(writer, extension.Extension == "(no extension)" ? text.NoExtension : extension.Extension);
            WriteNumberCell(writer, extension.Size / 1048576d, style: 2);
            WriteNumberCell(writer, extension.Size, style: 3);
            WriteNumberCell(writer, extension.AllocatedSize, style: 3);
            WriteNumberCell(writer, extension.Percentage / 100d, style: 4);
            WriteNumberCell(writer, extension.FileCount, style: 3);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteColumns(XmlWriter writer, IReadOnlyList<double> widths)
    {
        writer.WriteStartElement("cols");
        for (var index = 0; index < widths.Count; index++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", widths[index].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteHeaderRow(XmlWriter writer, IEnumerable<string> titles)
    {
        writer.WriteStartElement("row");
        foreach (var title in titles)
        {
            WriteStringCell(writer, title, style: 1);
        }
        writer.WriteEndElement();
    }

    private static void WriteStringCell(XmlWriter writer, string value, int style = 0)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("t", "inlineStr");
        if (style != 0)
        {
            writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteStartElement("is");
        writer.WriteStartElement("t");
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(Sanitize(value));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteNumberCell(XmlWriter writer, double value, int style = 0)
    {
        writer.WriteStartElement("c");
        if (style != 0)
        {
            writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteElementString("v", value.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WriteDateCell(XmlWriter writer, DateTimeOffset value)
    {
        if (value == default)
        {
            writer.WriteStartElement("c");
            writer.WriteEndElement();
            return;
        }
        // Excel stores local wall-clock time as days since 1899-12-30.
        WriteNumberCell(writer, value.LocalDateTime.ToOADate(), style: 37);
    }

    private static XmlWriter CreateXmlWriter(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        return XmlWriter.Create(entry.Open(), new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = true
        });
    }

    private static void WritePart(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content.Trim());
        stream.Write(bytes);
    }

    // Excel sheet names: at most 31 characters, none of : \ / ? * [ ].
    private static string SheetName(string name)
    {
        var cleaned = new string(name.Select(static character => character is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '_' : character).ToArray()).Trim('\'', ' ');
        if (cleaned.Length == 0)
        {
            cleaned = "DiskLoom";
        }
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string XmlAttribute(string value) => WebUtility.HtmlEncode(Sanitize(value));

    // NTFS allows names that are not valid UTF-16 (lone surrogates); XML and HTML do not.
    private static string Sanitize(string value)
    {
        var needsFix = false;
        for (var index = 0; index < value.Length && !needsFix; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(character) || (character < 0x20 && character is not ('\t' or '\n' or '\r')))
            {
                needsFix = true;
            }
        }
        if (!needsFix)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(character).Append(value[++index]);
            }
            else if (char.IsSurrogate(character) || (character < 0x20 && character is not ('\t' or '\n' or '\r')))
            {
                builder.Append('�');
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // Style indices: 0 default, 1 header, 2 MB (0.0), 3 integer with separators, 4 percent,
    // 5–20 file names indented 0–15, 21–36 bold folder names indented 0–15, 37 date/time.
    private static readonly string StylesXml = BuildStylesXml();

    private static string BuildStylesXml()
    {
        var cells = new StringBuilder();
        cells.Append("""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""");
        cells.Append("""<xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>""");
        cells.Append("""<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>""");
        cells.Append("""<xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>""");
        cells.Append("""<xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>""");
        for (var indent = 0; indent < 16; indent++)
        {
            cells.Append($"""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment indent="{indent}"/></xf>""");
        }
        for (var indent = 0; indent < 16; indent++)
        {
            cells.Append($"""<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment indent="{indent}"/></xf>""");
        }
        cells.Append("""<xf numFmtId="22" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>""");

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="2"><numFmt numFmtId="164" formatCode="#,##0.0"/><numFmt numFmtId="165" formatCode="0.0%"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font><font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDDEBFA"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="38">{cells}</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
            """;
    }
}
