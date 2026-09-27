using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

var testRoot = Path.Combine(Path.GetTempPath(), "DiskLoom.SmokeTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    var firstDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "First"));
    var secondDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "Second"));
    var excludedDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "SkipMe"));
    var duplicateBytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
    await File.WriteAllBytesAsync(Path.Combine(firstDirectory.FullName, "one.bin"), duplicateBytes);
    await File.WriteAllBytesAsync(Path.Combine(secondDirectory.FullName, "two.bin"), duplicateBytes);
    Assert(
        NativeTestMethods.CreateHardLink(
            Path.Combine(secondDirectory.FullName, "one-link.bin"),
            Path.Combine(firstDirectory.FullName, "one.bin"),
            IntPtr.Zero),
        "Could not create the hard-link scan fixture.");
    await File.WriteAllTextAsync(Path.Combine(secondDirectory.FullName, "unique.txt"), "DiskLoom smoke test");
    await File.WriteAllTextAsync(Path.Combine(excludedDirectory.FullName, "ignored.txt"), "ignored");

    var scanner = new FileSystemScanner();
    var result = await scanner.ScanAsync(testRoot, new ScanOptions
    {
        Parallelism = 2,
        CalculateAllocatedSize = true,
        ExcludePatterns = ["SkipMe"]
    });

    Assert(result.Root.FileCount == 4, $"Expected 4 files, found {result.Root.FileCount}.");
    Assert(result.Root.FolderCount == 2, $"Expected 2 folders, found {result.Root.FolderCount}.");
    Assert(result.Root.Size == duplicateBytes.LongLength * 3 + "DiskLoom smoke test".Length, "Logical size aggregate is incorrect.");
    Assert(result.Root.Files().Where(static file => !file.IsAdditionalHardLink).All(static file => file.AllocatedSize >= file.Size), "Allocated size should cover these ordinary test files.");
    Assert(result.Root.Files().Count(static file => file.IsAdditionalHardLink) == 1, "Hard-link allocation was not deduplicated.");
    Assert(result.Root.Files().Where(static file => file.IsAdditionalHardLink).All(static file => file.AllocatedSize == 0), "Additional hard links must not allocate the data twice.");
    Assert(result.Extensions.Any(static extension => extension.Extension == ".bin" && extension.FileCount == 3), "Extension statistics are incorrect.");
    Assert(result.Root.Files().All(static file => file.VolumeSerialNumber != 0), "Scanned files must record their volume so file IDs from different volumes cannot collide.");

    var duplicateResult = await new DuplicateFinder().FindAsync(result.Root, minimumFileSize: 1);
    Assert(duplicateResult.Groups.Count == 1, $"Expected one duplicate group, found {duplicateResult.Groups.Count}.");
    Assert(duplicateResult.Groups[0].Files.Count == 2, "Duplicate group should contain two files.");

    var snapshotPath = Path.Combine(testRoot, "before.diskloom");
    var snapshotService = new SnapshotService();
    await snapshotService.SaveAsync(result, snapshotPath);
    var before = await snapshotService.LoadAsync(snapshotPath);
    await File.AppendAllTextAsync(Path.Combine(secondDirectory.FullName, "unique.txt"), " changed");
    var afterResult = await scanner.ScanAsync(testRoot, new ScanOptions
    {
        Parallelism = 2,
        CalculateAllocatedSize = true,
        ExcludePatterns = ["SkipMe", "*.diskloom"]
    });
    var changes = snapshotService.Compare(before, snapshotService.Create(afterResult));
    Assert(changes.Any(change => change.RelativePath.EndsWith("unique.txt", StringComparison.OrdinalIgnoreCase) && change.Kind == ChangeKind.Changed), "Snapshot comparison missed a changed file.");

    var csvPath = Path.Combine(testRoot, "scan.csv");
    var jsonPath = Path.Combine(testRoot, "scan.json");
    var exporter = new ExportService();
    await exporter.ExportCsvAsync(afterResult, csvPath);
    await exporter.ExportJsonAsync(afterResult, jsonPath);
    Assert(File.ReadAllText(csvPath).Contains("unique.txt", StringComparison.Ordinal), "CSV export is missing a file.");
    Assert(File.ReadAllText(jsonPath).Contains("unique.txt", StringComparison.Ordinal), "JSON export is missing a file.");

    // Live results: with throttling off, every report after the root listing carries a snapshot
    // of the top-level entries whose sizes never exceed the final aggregated sizes.
    var liveSnapshots = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<ScanNode>>();
    var (savedProgressInterval, savedSnapshotInterval) = (FileSystemScanner.ProgressIntervalMilliseconds, FileSystemScanner.LiveSnapshotIntervalMilliseconds);
    FileSystemScanner.ProgressIntervalMilliseconds = 0;
    FileSystemScanner.LiveSnapshotIntervalMilliseconds = 0;
    ScanResult liveResult;
    try
    {
        liveResult = await scanner.ScanAsync(testRoot, new ScanOptions { Parallelism = 2, ExcludePatterns = ["SkipMe"] },
            new SynchronousProgress<ScanProgress>(value =>
            {
                if (value.TopLevel is { } topLevel)
                {
                    liveSnapshots.Add(topLevel);
                }
            }));
    }
    finally
    {
        (FileSystemScanner.ProgressIntervalMilliseconds, FileSystemScanner.LiveSnapshotIntervalMilliseconds) = (savedProgressInterval, savedSnapshotInterval);
    }
    Assert(!liveSnapshots.IsEmpty, "A scan must report live top-level results before it finishes.");
    var finalSizes = liveResult.Root.Children.ToDictionary(static child => child.FullPath, static child => child.Size, StringComparer.OrdinalIgnoreCase);
    Assert(liveSnapshots.All(snapshot => snapshot.All(entry => finalSizes.TryGetValue(entry.FullPath, out var final) && entry.Size <= final && entry.Children.Count == 0)),
        "Live entries must be detached top-level copies that never exceed the final size.");
    Assert(liveSnapshots.Any(static snapshot => snapshot.Any(static entry => entry.IsDirectory && entry.Size > 0)), "Live folder sizes must grow while their contents are scanned.");

    var binSearch = ScanSearch.Search(result.Root, new SearchCriteria { NamePattern = "*.bin" });
    Assert(binSearch.MatchCount == 3 && binSearch.Matches.All(static node => node.Extension == ".bin"), $"Wildcard search found {binSearch.MatchCount} .bin files, expected 3.");
    var substringSearch = ScanSearch.Search(result.Root, new SearchCriteria { NamePattern = "uniq", Kind = SearchItemKind.FilesAndFolders });
    Assert(substringSearch.MatchCount == 1, "A pattern without wildcards must match as a substring.");
    var folderSearch = ScanSearch.Search(result.Root, new SearchCriteria { Kind = SearchItemKind.Folders, MinimumSize = duplicateBytes.Length + 1 });
    Assert(folderSearch.Matches.Select(static node => node.Name).SequenceEqual(["Second"]), "Folder search with a size floor should return only 'Second'.");
    var cappedSearch = ScanSearch.Search(result.Root, new SearchCriteria { MaximumResults = 2 });
    Assert(cappedSearch.MatchCount == 4 && cappedSearch.Matches.Count == 2 && cappedSearch.IsTruncated &&
           cappedSearch.Matches[0].Size >= cappedSearch.Matches[1].Size, "Capped search must keep the largest matches, largest first, and count all of them.");
    var futureSearch = ScanSearch.Search(result.Root, new SearchCriteria { ModifiedAfter = DateTimeOffset.UtcNow.AddDays(1) });
    Assert(futureSearch.MatchCount == 0, "Date criteria must exclude files modified earlier.");
    Assert(ScanSearch.Search(result.Root, new SearchCriteria { Category = FileTypeCategory.Document }).MatchCount == 1, ".txt belongs to the Document category.");

    var textOnly = ScanTreeFilter.Apply(result, new TreeFilter { NamePattern = "*.txt" }, DateTimeOffset.UtcNow);
    Assert(textOnly.Root.FileCount == 1 && textOnly.Root.FolderCount == 1 && textOnly.Root.Size == "DiskLoom smoke test".Length,
        "The tree filter must keep only matching files and the folders that contain them.");
    Assert(textOnly.Root.Children.Single().Name == "Second" && result.Root.FileCount == 4, "Filtering must not modify the original tree.");
    Assert(textOnly.Extensions.Single().Extension == ".txt", "Filtered statistics must describe the filtered files.");
    var oldOnly = ScanTreeFilter.Apply(result, new TreeFilter { OlderThanDays = 1 }, DateTimeOffset.UtcNow);
    Assert(oldOnly.Root.FileCount == 0 && oldOnly.Root.Children.Count == 0, "Files created just now are not older than a day.");
    Assert(ReferenceEquals(ScanTreeFilter.Apply(result, new TreeFilter(), DateTimeOffset.UtcNow), result), "An empty filter must return the original result.");

    foreach (var kind in Enum.GetValues<BreakdownKind>())
    {
        var slices = FolderBreakdown.Create(result.Root, kind, maximumSlices: 2, DateTimeOffset.UtcNow);
        Assert(slices.Sum(static slice => slice.Size) == result.Root.Size, $"{kind} breakdown must account for the whole folder.");
    }
    Assert(FolderBreakdown.Create(result.Root, BreakdownKind.Children, 1, DateTimeOffset.UtcNow).Single().IsOther, "Surplus slices must fold into 'other'.");

    var shallowCsvPath = Path.Combine(testRoot, "shallow.csv");
    var shallow = await exporter.ExportCsvAsync(result, shallowCsvPath, new ExportOptions { MaximumDepth = 1, IncludeFiles = false });
    Assert(shallow.RowsWritten == 3 && File.ReadAllLines(shallowCsvPath).Length == 4, "Depth-1 folder export should list the root and its two folders.");
    var reports = new ReportExportService();
    var xlsxPath = Path.Combine(testRoot, "scan.xlsx");
    var xlsx = await reports.ExportXlsxAsync(result, xlsxPath, new ExportOptions());
    Assert(xlsx.RowsWritten == 7 && !xlsx.IsTruncated, $"Excel export should write 7 rows (root, 2 folders, 4 files), wrote {xlsx.RowsWritten}.");
    using (var workbook = System.IO.Compression.ZipFile.OpenRead(xlsxPath))
    {
        foreach (var part in new[] { "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" })
        {
            using var reader = new StreamReader(workbook.GetEntry(part)!.Open());
            var document = System.Xml.Linq.XDocument.Parse(await reader.ReadToEndAsync());
            Assert(document.Root is not null, $"{part} must be well-formed XML.");
            if (part.EndsWith("sheet1.xml", StringComparison.Ordinal))
            {
                Assert(document.ToString().Contains("unique.txt", StringComparison.Ordinal), "Excel export is missing a file.");
            }
        }
    }
    var cappedXlsx = await reports.ExportXlsxAsync(result, Path.Combine(testRoot, "capped.xlsx"), new ExportOptions { MaximumRows = 2 });
    Assert(cappedXlsx.RowsWritten == 2 && cappedXlsx.IsTruncated, "A row limit must truncate the export and say so.");
    var htmlPath = Path.Combine(testRoot, "scan.html");
    var html = await reports.ExportHtmlAsync(result, htmlPath, new ExportOptions { MaximumDepth = 1 });
    var htmlText = await File.ReadAllTextAsync(htmlPath);
    Assert(html.RowsWritten == 3 && htmlText.Contains("Second", StringComparison.Ordinal) && !htmlText.Contains("unique.txt", StringComparison.Ordinal),
        "Depth-1 HTML report should show the top folders but not the files inside them.");
    var escapedRoot = new ScanNode { Name = "<b>&", FullPath = "C:\\<b>&", IsDirectory = true };
    var (escapedHtml, _) = ReportExportService.BuildHtml(new ScanResult(escapedRoot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], []), new ExportOptions(), new ReportText(), System.Globalization.CultureInfo.InvariantCulture);
    Assert(!escapedHtml.Contains("<b>&", StringComparison.Ordinal) && escapedHtml.Contains("&lt;b&gt;&amp;", StringComparison.Ordinal), "HTML report must escape file names.");

    var fileOperations = new FileOperationService();
    var copyTarget = Directory.CreateDirectory(Path.Combine(testRoot, "CopyTarget")).FullName;
    var copied = await fileOperations.CopyAsync([Path.Combine(secondDirectory.FullName, "unique.txt")], copyTarget, nint.Zero);
    Assert(copied.Succeeded && File.Exists(Path.Combine(copyTarget, "unique.txt")) && File.Exists(Path.Combine(secondDirectory.FullName, "unique.txt")), "Copy must leave the source and create the target.");
    var moveTarget = Directory.CreateDirectory(Path.Combine(testRoot, "MoveTarget")).FullName;
    var moved = await fileOperations.MoveAsync([Path.Combine(copyTarget, "unique.txt")], moveTarget, nint.Zero);
    Assert(moved.Succeeded && File.Exists(Path.Combine(moveTarget, "unique.txt")) && !File.Exists(Path.Combine(copyTarget, "unique.txt")), "Move must remove the source.");

    var driveRoot = new ScanNode { Name = "C:\\", FullPath = "C:\\", IsDirectory = true };
    driveRoot.Children.Add(new ScanNode { Name = "Windows", FullPath = "C:\\Windows", IsDirectory = true });
    var driveResult = new ScanResult(driveRoot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], []);
    var originalDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var fromWindows = snapshotService.Create(driveResult);
        Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var fromSystem = snapshotService.Create(driveResult);
        Assert(fromWindows.Entries.Select(static entry => entry.RelativePath).SequenceEqual([".", "Windows"]), "Drive-root snapshot paths must be relative to the drive root.");
        Assert(snapshotService.Compare(fromWindows, fromSystem).Count == 0, "Drive-root snapshots must not depend on the working directory.");
    }
    finally
    {
        Environment.CurrentDirectory = originalDirectory;
    }

    var legacySnapshotPath = Path.Combine(testRoot, "legacy.diskloom");
    await using (var legacyFile = File.Create(legacySnapshotPath))
    await using (var legacyGzip = new System.IO.Compression.GZipStream(legacyFile, System.IO.Compression.CompressionLevel.Fastest))
    {
        await System.Text.Json.JsonSerializer.SerializeAsync(
            legacyGzip,
            new ScanSnapshot("C:\\", DateTimeOffset.UtcNow, [new SnapshotEntry("..", true, 0, 0, 0)]),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
    await AssertThrowsAsync<InvalidDataException>(() => snapshotService.LoadAsync(legacySnapshotPath));

    var deepRoot = new ScanNode { Name = "C:\\", FullPath = "C:\\", IsDirectory = true };
    var deepNode = deepRoot;
    for (var level = 0; level < 60; level++)
    {
        var next = new ScanNode { Name = $"d{level}", FullPath = Path.Combine(deepNode.FullPath, $"d{level}"), IsDirectory = true };
        deepNode.Children.Add(next);
        deepNode = next;
    }
    var deepJsonPath = Path.Combine(testRoot, "deep.json");
    await exporter.ExportJsonAsync(new ScanResult(deepRoot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], []), deepJsonPath);
    Assert(File.ReadAllText(deepJsonPath).Contains("\"d59\"", StringComparison.Ordinal), "JSON export must handle deeply nested folders.");

    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(testRoot, cancellationToken: canceled.Token));

    var updateCheck = await new UpdateService().CheckAsync(new UpdateConfiguration(), new Version(0, 1, 0));
    Assert(!updateCheck.IsConfigured && !updateCheck.IsUpdateAvailable, "An empty update configuration must stay disabled.");

    var updateConfigurationPath = Path.Combine(testRoot, "update-config.json");
    await File.WriteAllTextAsync(updateConfigurationPath, """{"githubRepository":"Naxterra/DiskLoom","checkOnStartup":true,"checkIntervalHours":24}""");
    var loadedUpdateConfiguration = await UpdateService.LoadConfigurationAsync(updateConfigurationPath);
    Assert(loadedUpdateConfiguration.GitHubRepository == "Naxterra/DiskLoom", "Camel-case GitHub update configuration was not loaded.");

    const string githubRelease = """
        {
          "tag_name": "v0.2.0",
          "body": "GitHub release notes",
          "html_url": "https://github.com/Naxterra/DiskLoom/releases/tag/v0.2.0",
          "published_at": "2026-08-18T08:00:00Z",
          "assets": [
            {
              "name": "DiskLoom-Setup-x64-de-DE.msi",
              "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64-de-DE.msi",
              "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            },
            {
              "name": "DiskLoom-0.2.0-win-x64-portable.zip",
              "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-0.2.0-win-x64-portable.zip",
              "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            },
            {
              "name": "DiskLoom-Setup-x64.msi",
              "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64.msi",
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
          ]
        }
        """;
    using var githubClient = new HttpClient(new StubHttpMessageHandler(request =>
    {
        Assert(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/Naxterra/DiskLoom/releases/latest", "GitHub update endpoint is incorrect.");
        Assert(request.Headers.UserAgent.ToString().StartsWith("DiskLoom/", StringComparison.Ordinal), "GitHub request is missing its user agent.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(githubRelease, Encoding.UTF8, "application/json")
        };
    }));
    var githubCheck = await new UpdateService(githubClient).CheckAsync(
        new UpdateConfiguration { GitHubRepository = "Naxterra/DiskLoom" },
        new Version(0, 1, 4));
    Assert(githubCheck.IsConfigured && githubCheck.IsUpdateAvailable, "GitHub release should be detected as an update.");
    Assert(githubCheck.Manifest?.HasVerifiableInstaller == true, "GitHub asset digest should produce a verifiable installer manifest.");
    Assert(githubCheck.Manifest!.InstallerUrl.EndsWith("/DiskLoom-Setup-x64.msi", StringComparison.Ordinal) && githubCheck.Manifest.Sha256.StartsWith('a'),
        "Without a language preference the English MSI must be chosen.");
    var germanCheck = await new UpdateService(githubClient).CheckAsync(
        new UpdateConfiguration { GitHubRepository = "Naxterra/DiskLoom" }, new Version(0, 1, 4), preferredLanguage: "de-DE");
    Assert(germanCheck.Manifest!.InstallerUrl.EndsWith("/DiskLoom-Setup-x64-de-DE.msi", StringComparison.Ordinal) && germanCheck.Manifest.Sha256.StartsWith('b'),
        "The German UI must update with the German MSI.");
    var repositoryConfiguration = new UpdateConfiguration { GitHubRepository = "Naxterra/DiskLoom" };
    Assert(UpdateService.CanInstallDirectly(githubCheck.Manifest, repositoryConfiguration), "An MSI from the configured repository's release must be installable without a pinned certificate.");
    Assert(!UpdateService.CanInstallDirectly(githubCheck.Manifest with { InstallerUrl = "https://example.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64.msi" }, repositoryConfiguration),
        "An installer hosted anywhere but the repository's GitHub releases must not install automatically.");
    Assert(!UpdateService.CanInstallDirectly(githubCheck.Manifest, new UpdateConfiguration { GitHubRepository = "Someone/Else" }), "Another repository's download must not install automatically.");
    Assert(!UpdateService.CanInstallDirectly(githubCheck.Manifest with { Sha256 = string.Empty }, repositoryConfiguration), "An installer without a SHA-256 digest must not install automatically.");
    var notAnMsi = Path.Combine(testRoot, "fake.msi");
    await File.WriteAllTextAsync(notAnMsi, "not a Windows Installer package");
    Assert(!UpdateService.IsExpectedMsi(notAnMsi, "0.2.0", out _), "A file that is not DiskLoom's MSI must be rejected.");
    Assert(githubCheck.Manifest?.ReleaseNotesUrl.EndsWith("/v0.2.0", StringComparison.Ordinal) == true, "GitHub release page was not retained.");

    using var noReleaseClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
    var noReleaseCheck = await new UpdateService(noReleaseClient).CheckAsync(
        new UpdateConfiguration { GitHubRepository = "Naxterra/DiskLoom" },
        new Version(0, 1, 4));
    Assert(noReleaseCheck.IsConfigured && !noReleaseCheck.IsUpdateAvailable, "A repository without releases should be a successful no-update result.");

    Console.WriteLine("DiskLoom core smoke tests passed.");
    return 0;
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}

static partial class NativeTestMethods
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

// Progress<T> posts to the thread pool; tests need every report delivered before ScanAsync returns.
sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
