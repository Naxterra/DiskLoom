using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

// Named scenario tests over shared fixtures. Each test prints "Program.cs::name", its scenario and PASS or FAIL;
// a failing test does not stop the ones after it, and the exit code is 1 if any test failed.
var tests = new List<(string Name, string Scenario, Func<Task> Body)>();
void Test(string name, string scenario, Func<Task> body) => tests.Add((name, scenario, body));
void TestSync(string name, string scenario, Action body) => tests.Add((name, scenario, () => { body(); return Task.CompletedTask; }));

var testRoot = Path.Combine(Path.GetTempPath(), "DiskLoom.SmokeTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    // Shared fixture: two folders, a hard link, two identical files and one excluded folder.
    var firstDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "First"));
    var secondDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "Second"));
    var excludedDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "SkipMe"));
    var duplicateBytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
    await File.WriteAllBytesAsync(Path.Combine(firstDirectory.FullName, "one.bin"), duplicateBytes);
    await File.WriteAllBytesAsync(Path.Combine(secondDirectory.FullName, "two.bin"), duplicateBytes);
    Require(NativeTestMethods.CreateHardLink(Path.Combine(secondDirectory.FullName, "one-link.bin"), Path.Combine(firstDirectory.FullName, "one.bin"), IntPtr.Zero),
        "Could not create the hard-link scan fixture.");
    await File.WriteAllTextAsync(Path.Combine(secondDirectory.FullName, "unique.txt"), "DiskLoom smoke test");
    await File.WriteAllTextAsync(Path.Combine(excludedDirectory.FullName, "ignored.txt"), "ignored");

    var scanner = new FileSystemScanner();
    var result = await scanner.ScanAsync(testRoot, new ScanOptions { Parallelism = 2, CalculateAllocatedSize = true, ExcludePatterns = ["SkipMe"] });
    var snapshotService = new SnapshotService();
    var exporter = new ExportService();
    var reports = new ReportExportService();
    var fileOperations = new FileOperationService();

    // ---- Scanning ------------------------------------------------------------------------------

    TestSync("scan_counts_files_and_folders_without_excluded_folder", "A scan that excludes SkipMe counts four files in two folders.", () =>
    {
        Require(result.Root.FileCount == 4, $"Expected 4 files, found {result.Root.FileCount}.");
        Require(result.Root.FolderCount == 2, $"Expected 2 folders, found {result.Root.FolderCount}.");
    });
    TestSync("scan_sums_logical_size_of_all_files", "The scan root's logical size is the sum of its files, hard links included.", () =>
        Require(result.Root.Size == duplicateBytes.LongLength * 3 + "DiskLoom smoke test".Length, "Logical size aggregate is incorrect."));
    TestSync("scan_allocation_covers_ordinary_files", "Every ordinary file is allocated at least its logical size.", () =>
        Require(result.Root.Files().Where(static file => !file.IsAdditionalHardLink).All(static file => file.AllocatedSize >= file.Size), "Allocated size should cover these ordinary test files."));
    TestSync("hard_link_data_is_allocated_once", "Of two paths to one file, only one carries the allocation and the other counts zero.", () =>
    {
        Require(result.Root.Files().Count(static file => file.IsAdditionalHardLink) == 1, "Hard-link allocation was not deduplicated.");
        Require(result.Root.Files().Where(static file => file.IsAdditionalHardLink).All(static file => file.AllocatedSize == 0), "Additional hard links must not allocate the data twice.");
    });
    TestSync("extension_statistics_count_files_per_extension", "The .bin extension is counted with its three files.", () =>
        Require(result.Extensions.Any(static extension => extension.Extension == ".bin" && extension.FileCount == 3), "Extension statistics are incorrect."));
    TestSync("scanned_files_record_their_volume", "Every scanned file records its volume serial, so file IDs from different volumes cannot collide.", () =>
        Require(result.Root.Files().All(static file => file.VolumeSerialNumber != 0), "Scanned files must record their volume."));

    Test("live_results_are_reported_while_scanning", "With throttling off, a scan reports detached top-level entries whose sizes grow and never exceed the final sizes.", async () =>
    {
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
        Require(!liveSnapshots.IsEmpty, "A scan must report live top-level results before it finishes.");
        var finalSizes = liveResult.Root.Children.ToDictionary(static child => child.FullPath, static child => child.Size, StringComparer.OrdinalIgnoreCase);
        Require(liveSnapshots.All(snapshot => snapshot.All(entry => finalSizes.TryGetValue(entry.FullPath, out var final) && entry.Size <= final && entry.Children.Count == 0)),
            "Live entries must be detached top-level copies that never exceed the final size.");
        Require(liveSnapshots.Any(static snapshot => snapshot.Any(static entry => entry.IsDirectory && entry.Size > 0)), "Live folder sizes must grow while their contents are scanned.");
    });

    Test("canceled_scan_throws_operation_canceled", "A scan started with an already canceled token stops with OperationCanceledException.", async () =>
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await RequireThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(testRoot, cancellationToken: canceled.Token));
    });

    TestSync("drive_letter_without_separator_means_drive_root", "\"C:\" and the command-line artifact C:\" both normalize to C:\\, and quotes are removed.", () =>
        Require(ScanPaths.Normalize("C:") == @"C:\" && ScanPaths.Normalize("C:\"") == @"C:\" && ScanPaths.Normalize(@"""C:\Windows""") == @"C:\Windows",
            "Drive letters without a separator must normalize to the drive root."));

    Test("followed_links_never_count_a_folder_twice", "With links followed, a link to a folder, a link to its parent and a link back to the scan root count the one file once.", async () =>
    {
        var links = Path.Combine(testRoot, "Junctions");
        var docs = Directory.CreateDirectory(Path.Combine(links, "Outside", "X", "Docs")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(docs, "f.bin"), new byte[1024]);
        var scanRoot = Directory.CreateDirectory(Path.Combine(links, "Scan")).FullName;
        CreateJunction(Path.Combine(scanRoot, "J"), docs);
        CreateJunction(Path.Combine(scanRoot, "K"), Path.Combine(links, "Outside", "X"));
        CreateJunction(Path.Combine(scanRoot, "Loop"), scanRoot);
        var followed = await scanner.ScanAsync(scanRoot, new ScanOptions { Parallelism = 4, FollowReparsePoints = true });
        Require(followed.Root.FileCount == 1 && followed.Root.Size == 1024, $"Expected one file of 1024 bytes, found {followed.Root.FileCount} files of {followed.Root.Size} bytes.");
    });
    Test("drive_and_volume_roots_are_refused_by_file_operations", "Deleting, recycling or moving a drive root is refused before anything happens, and ordinary folders are not mistaken for volume roots.", async () =>
    {
        var systemRoot = Path.GetPathRoot(testRoot)!;
        Require(WindowsPaths.IsVolumeRoot(systemRoot) && WindowsPaths.IsVolumeRoot(systemRoot.TrimEnd('\\')) && WindowsPaths.IsVolumeRoot(@"\\?\" + systemRoot), "A drive root must be recognised in every spelling.");
        Require(!WindowsPaths.IsVolumeRoot(testRoot) && !WindowsPaths.IsVolumeRoot(firstDirectory.FullName), "Ordinary folders are not volume roots.");
        // An unused drive letter, so a missing refusal fails with "not found" instead of touching a real drive.
        var unused = "QRSTUVWXYZ".Select(static letter => $"{letter}:\\").First(static root => !Directory.Exists(root));
        await RequireThrowsAsync<InvalidOperationException>(() => fileOperations.DeletePermanentlyAsync(unused));
        await RequireThrowsAsync<InvalidOperationException>(() => fileOperations.RecycleAsync(unused));
        await RequireThrowsAsync<InvalidOperationException>(() => fileOperations.MoveAsync([unused], testRoot, 0));
    });

    // ---- Names ending in a dot or space (Windows would trim them to a different item) ---------

    var trimmedRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Trimmed")).FullName;
    var plainFile = Path.Combine(trimmedRoot, "a");
    var dottedFile = plainFile + ".";
    var plainFolder = Path.Combine(trimmedRoot, "b");
    var spacedFolder = plainFolder + " ";
    await File.WriteAllTextAsync(plainFile, "keep me");
    await File.WriteAllTextAsync(WindowsPaths.ToExtended(dottedFile), "dotted");
    Directory.CreateDirectory(plainFolder);
    await File.WriteAllTextAsync(Path.Combine(plainFolder, "keep.txt"), "keep me too");
    Directory.CreateDirectory(WindowsPaths.ToExtended(spacedFolder));
    await File.WriteAllTextAsync(WindowsPaths.ToExtended(Path.Combine(spacedFolder, "other.txt")), "in the folder whose name ends in a space");

    Test("scan_lists_folder_ending_in_space_with_its_own_contents", "A folder named \"b \" next to \"b\" is scanned as itself and shows its own file, not the twin's.", async () =>
    {
        var scan = await scanner.ScanAsync(trimmedRoot);
        var spaced = ScanTreeEditor.Find(scan.Root, spacedFolder);
        var plain = ScanTreeEditor.Find(scan.Root, plainFolder);
        Require(spaced is not null && plain is not null, "Both folders must appear in the scan.");
        Require(spaced!.Children.Select(static child => child.Name).SequenceEqual(["other.txt"]) && plain!.Children.Select(static child => child.Name).SequenceEqual(["keep.txt"]),
            "Each folder must list its own file.");
    });
    Test("recycle_refuses_name_ending_in_dot_and_touches_nothing", "Moving \"a.\" to the Recycle Bin is refused before anything happens, and both \"a\" and \"a.\" stay.", async () =>
    {
        await RequireThrowsAsync<IOException>(() => fileOperations.RecycleAsync(dottedFile));
        Require(File.Exists(plainFile) && File.Exists(WindowsPaths.ToExtended(dottedFile)), "No file may be touched.");
    });
    Test("copy_refuses_folder_ending_in_space", "Copying \"b \" with Explorer's engine is refused, because it would copy \"b\" instead.", async () =>
        await RequireThrowsAsync<IOException>(() => fileOperations.CopyAsync([spacedFolder], testRoot, nint.Zero)));
    Test("permanent_delete_of_name_ending_in_dot_removes_only_that_file", "Deleting \"a.\" permanently removes exactly \"a.\" and leaves \"a\".", async () =>
    {
        await fileOperations.DeletePermanentlyAsync(dottedFile);
        Require(!File.Exists(WindowsPaths.ToExtended(dottedFile)) && File.ReadAllText(plainFile) == "keep me", "Only the dotted file may be deleted.");
    });
    Test("permanent_delete_of_folder_ending_in_space_keeps_its_twin", "Deleting folder \"b \" permanently removes it with its contents and leaves \"b\\keep.txt\".", async () =>
    {
        await fileOperations.DeletePermanentlyAsync(spacedFolder);
        Require(!Directory.Exists(WindowsPaths.ToExtended(spacedFolder)) && File.Exists(Path.Combine(plainFolder, "keep.txt")), "Only the folder ending in a space may be deleted.");
    });
    TestSync("trimmed_segment_detection_checks_every_segment", "A dot or space at the end of any path segment is detected, but \".\" and \"..\" are not.", () =>
        Require(WindowsPaths.HasTrimmedSegment(@"C:\x\b \file.txt") && WindowsPaths.HasTrimmedSegment(@"C:\x\a.") &&
                !WindowsPaths.HasTrimmedSegment(@"C:\x\a.txt") && !WindowsPaths.HasTrimmedSegment(@"C:\x\..\y"), "Trimmed-name detection is wrong."));

    // ---- Search, filter and charts ---------------------------------------------------------------

    TestSync("wildcard_search_finds_matching_files", "Searching *.bin finds exactly the three .bin files.", () =>
    {
        var binSearch = ScanSearch.Search(result.Root, new SearchCriteria { NamePattern = "*.bin" });
        Require(binSearch.MatchCount == 3 && binSearch.Matches.All(static node => node.Extension == ".bin"), $"Wildcard search found {binSearch.MatchCount} .bin files, expected 3.");
    });
    TestSync("search_without_wildcards_matches_substring", "Searching \"uniq\" finds unique.txt.", () =>
        Require(ScanSearch.Search(result.Root, new SearchCriteria { NamePattern = "uniq", Kind = SearchItemKind.FilesAndFolders }).MatchCount == 1, "A pattern without wildcards must match as a substring."));
    TestSync("folder_search_respects_minimum_size", "A folder search with a size floor returns only the larger folder.", () =>
        Require(ScanSearch.Search(result.Root, new SearchCriteria { Kind = SearchItemKind.Folders, MinimumSize = duplicateBytes.Length + 1 }).Matches.Select(static node => node.Name).SequenceEqual(["Second"]),
            "Folder search with a size floor should return only 'Second'."));
    TestSync("capped_search_keeps_largest_and_counts_all", "A search capped at two results keeps the two largest, largest first, and counts all four.", () =>
    {
        var capped = ScanSearch.Search(result.Root, new SearchCriteria { MaximumResults = 2 });
        Require(capped.MatchCount == 4 && capped.Matches.Count == 2 && capped.IsTruncated && capped.Matches[0].Size >= capped.Matches[1].Size, "Capped search is wrong.");
    });
    TestSync("date_search_excludes_older_files", "Searching for files modified after tomorrow finds nothing.", () =>
        Require(ScanSearch.Search(result.Root, new SearchCriteria { ModifiedAfter = DateTimeOffset.UtcNow.AddDays(1) }).MatchCount == 0, "Date criteria must exclude files modified earlier."));
    TestSync("type_search_finds_documents", "Searching the Document category finds the one .txt file.", () =>
        Require(ScanSearch.Search(result.Root, new SearchCriteria { Category = FileTypeCategory.Document }).MatchCount == 1, ".txt belongs to the Document category."));

    TestSync("tree_filter_keeps_only_matching_files", "Filtering *.txt keeps one file in one folder and leaves the original tree unchanged.", () =>
    {
        var textOnly = ScanTreeFilter.Apply(result, new TreeFilter { NamePattern = "*.txt" }, DateTimeOffset.UtcNow);
        Require(textOnly.Root.FileCount == 1 && textOnly.Root.FolderCount == 1 && textOnly.Root.Size == "DiskLoom smoke test".Length, "The filter must keep only matching files and their folders.");
        Require(textOnly.Root.Children.Single().Name == "Second" && result.Root.FileCount == 4, "Filtering must not modify the original tree.");
        Require(textOnly.Extensions.Single().Extension == ".txt", "Filtered statistics must describe the filtered files.");
    });
    TestSync("age_filter_excludes_new_files", "Filtering for files older than a day removes the freshly created ones.", () =>
    {
        var oldOnly = ScanTreeFilter.Apply(result, new TreeFilter { OlderThanDays = 1 }, DateTimeOffset.UtcNow);
        Require(oldOnly.Root.FileCount == 0 && oldOnly.Root.Children.Count == 0, "Files created just now are not older than a day.");
    });
    TestSync("empty_filter_returns_original_result", "An empty filter returns the original scan object.", () =>
        Require(ReferenceEquals(ScanTreeFilter.Apply(result, new TreeFilter(), DateTimeOffset.UtcNow), result), "An empty filter must return the original result."));
    TestSync("chart_breakdowns_account_for_whole_folder", "Every breakdown kind adds up to the folder's size, and surplus slices fold into one other slice.", () =>
    {
        foreach (var kind in Enum.GetValues<BreakdownKind>())
        {
            Require(FolderBreakdown.Create(result.Root, kind, maximumSlices: 2, DateTimeOffset.UtcNow).Sum(static slice => slice.Size) == result.Root.Size, $"{kind} breakdown must account for the whole folder.");
        }
        Require(FolderBreakdown.Create(result.Root, BreakdownKind.Children, 1, DateTimeOffset.UtcNow).Single().IsOther, "Surplus slices must fold into 'other'.");
    });

    // ---- Duplicates ---------------------------------------------------------------------------------

    Test("duplicate_search_groups_identical_files", "The two identical files form one group, and the hard link to one of them is not counted as a copy.", async () =>
    {
        var duplicates = await new DuplicateFinder().FindAsync(result.Root, minimumFileSize: 1);
        Require(duplicates.Groups.Count == 1 && duplicates.Groups[0].Files.Count == 2, "Expected one duplicate group with two files.");
    });
    Test("duplicate_search_skips_files_changed_after_scan", "Files shrunk after the scan are skipped and reported, not grouped with their old size.", async () =>
    {
        var changedDuplicates = Directory.CreateDirectory(Path.Combine(testRoot, "Changed")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(changedDuplicates, "a.bin"), duplicateBytes);
        await File.WriteAllBytesAsync(Path.Combine(changedDuplicates, "b.bin"), duplicateBytes);
        var changedScan = await scanner.ScanAsync(changedDuplicates);
        await File.WriteAllBytesAsync(Path.Combine(changedDuplicates, "a.bin"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(changedDuplicates, "b.bin"), [1]);
        var changed = await new DuplicateFinder().FindAsync(changedScan.Root, minimumFileSize: 1);
        Require(changed.Groups.Count == 0 && changed.Issues.Count == 2 && changed.Issues.All(static issue => issue.ErrorType == "ChangedSinceScan"), "Changed files must be skipped and reported.");
    });
    TestSync("duplicate_delete_revalidation_detects_changes", "A duplicate whose size no longer matches the check is reported as changed.", () =>
    {
        var path = Path.Combine(firstDirectory.FullName, "one.bin");
        var unchangedCopy = new DuplicateFile(path, duplicateBytes.Length, File.GetLastWriteTimeUtc(path));
        Require(DuplicateFinder.IsUnchanged(unchangedCopy) && !DuplicateFinder.IsUnchanged(unchangedCopy with { Size = 1 }), "Delete-time revalidation must detect a changed duplicate.");
    });
    Test("duplicate_search_never_reads_cloud_only_files", "Online-only cloud files are skipped and counted without being opened.", async () =>
    {
        var cloudRoot = new ScanNode { Name = "cloud", FullPath = firstDirectory.FullName, IsDirectory = true };
        cloudRoot.Children.Add(new ScanNode { Name = "one.bin", FullPath = Path.Combine(firstDirectory.FullName, "one.bin"), IsDirectory = false, Size = duplicateBytes.Length, Attributes = (FileAttributes)0x00400000 });
        cloudRoot.Children.Add(new ScanNode { Name = "two.bin", FullPath = Path.Combine(secondDirectory.FullName, "two.bin"), IsDirectory = false, Size = duplicateBytes.Length, Attributes = FileAttributes.Offline });
        var cloud = await new DuplicateFinder().FindAsync(cloudRoot, minimumFileSize: 1);
        Require(cloud.SkippedCloudFiles == 2 && cloud.Groups.Count == 0 && cloud.Issues.Count == 0, "Online-only cloud files must be skipped without being opened.");
    });
    TestSync("deleting_every_copy_of_a_duplicate_group_is_refused", "Selecting all remaining copies of a group for deletion is flagged, so at least one copy is kept.", () =>
    {
        var group = new DuplicateGroup("hash", 10, [new DuplicateFile(@"C:\d\a.bin", 10, default), new DuplicateFile(@"C:\d\b.bin", 10, default), new DuplicateFile(@"C:\d\c.bin", 10, default)]);
        var remaining = new[] { @"C:\d\a.bin", @"C:\d\b.bin" };
        Require(DuplicateFinder.GroupsLosingEveryCopy([group], remaining, [@"C:\d\a.bin", @"C:\d\b.bin"]).Count == 1, "Deleting both remaining copies must be flagged.");
    });
    Test("deleting_some_copies_of_a_duplicate_group_is_allowed", "Deleting one of two copies that are both still on disk and unchanged is not flagged.", async () =>
    {
        var group = await DuplicatePairAsync("KeepOne");
        Require(DuplicateFinder.GroupsLosingEveryCopy([group], group.Files.Select(static file => file.Path), [group.Files[0].Path]).Count == 0, "Deleting one copy must be allowed.");
    });
    Test("deleting_last_copy_after_other_copy_vanished_is_refused", "When another program deleted one copy after the check, deleting the other, still listed copy is flagged.", async () =>
    {
        var group = await DuplicatePairAsync("Vanished");
        File.Delete(group.Files[1].Path);
        Require(DuplicateFinder.GroupsLosingEveryCopy([group], group.Files.Select(static file => file.Path), [group.Files[0].Path]).Count == 1, "Deleting the last copy on disk must be flagged.");
    });
    Test("duplicate_rewritten_with_same_size_and_date_counts_as_changed", "A copy overwritten with other content of the same size, with its modification time set back, no longer counts as unchanged.", async () =>
    {
        var group = await DuplicatePairAsync("Rewritten");
        var rewritten = group.Files[0];
        await File.WriteAllBytesAsync(rewritten.Path, duplicateBytes.Reverse().ToArray());
        File.SetLastWriteTimeUtc(rewritten.Path, rewritten.LastWriteUtc.UtcDateTime);
        Require(new FileInfo(rewritten.Path).Length == rewritten.Size && File.GetLastWriteTimeUtc(rewritten.Path) == rewritten.LastWriteUtc.UtcDateTime, "The fixture must keep size and date.");
        Require(!DuplicateFinder.IsUnchanged(rewritten), "A rewritten copy must count as changed.");
        Require(DuplicateFinder.IsUnchanged(group.Files[1]), "The untouched copy must still count as unchanged.");
        Require(DuplicateFinder.GroupsLosingEveryCopy([group], group.Files.Select(static file => file.Path), [group.Files[1].Path]).Count == 1, "Deleting the only intact copy must be flagged.");
    });

    // ---- Snapshots -------------------------------------------------------------------------------------

    Test("snapshot_compare_finds_changed_file", "After a file grows, comparing the old snapshot with a new scan reports it as changed.", async () =>
    {
        var snapshotPath = Path.Combine(testRoot, "before.diskloom");
        await snapshotService.SaveAsync(result, snapshotPath);
        var before = await snapshotService.LoadAsync(snapshotPath);
        await File.AppendAllTextAsync(Path.Combine(secondDirectory.FullName, "unique.txt"), " changed");
        var after = await scanner.ScanAsync(testRoot, new ScanOptions { Parallelism = 2, ExcludePatterns = ["SkipMe", "*.diskloom", "Trimmed"] });
        Require(snapshotService.Compare(before, snapshotService.Create(after)).Any(static change => change.RelativePath.EndsWith("unique.txt", StringComparison.OrdinalIgnoreCase) && change.Kind == ChangeKind.Changed),
            "Snapshot comparison missed a changed file.");
    });
    TestSync("drive_root_snapshot_is_independent_of_working_directory", "A drive-root snapshot has the same relative paths whatever the current directory is.", () =>
    {
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
            Require(fromWindows.Entries.Select(static entry => entry.RelativePath).SequenceEqual([".", "Windows"]), "Drive-root snapshot paths must be relative to the drive root.");
            Require(snapshotService.Compare(fromWindows, fromSystem).Count == 0, "Drive-root snapshots must not depend on the working directory.");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    });
    Test("legacy_broken_drive_root_snapshot_is_rejected", "A snapshot saved by the old drive-root bug is refused with InvalidDataException.", async () =>
    {
        var legacySnapshotPath = Path.Combine(testRoot, "legacy.diskloom");
        await using (var legacyFile = File.Create(legacySnapshotPath))
        await using (var legacyGzip = new System.IO.Compression.GZipStream(legacyFile, System.IO.Compression.CompressionLevel.Fastest))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(legacyGzip, new ScanSnapshot("C:\\", DateTimeOffset.UtcNow, [new SnapshotEntry("..", true, 0, 0, 0)]),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        await RequireThrowsAsync<InvalidDataException>(() => snapshotService.LoadAsync(legacySnapshotPath));
    });
    Test("snapshot_save_canceled_before_writing_keeps_previous_snapshot", "Saving over an existing snapshot with a canceled token leaves the old file intact and no temporary file behind.", async () =>
    {
        var path = Path.Combine(testRoot, "keep.diskloom");
        await snapshotService.SaveAsync(result, path);
        var original = await File.ReadAllBytesAsync(path);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await RequireThrowsAsync<OperationCanceledException>(() => snapshotService.SaveAsync(result, path, canceled.Token));
        Require((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "The previous snapshot must be unchanged.");
        Require(!Directory.EnumerateFiles(testRoot, ".keep.diskloom.*.tmp").Any(), "No temporary file may be left behind.");
    });

    // ---- Exports ------------------------------------------------------------------------------------------

    Test("csv_and_json_exports_contain_every_file", "The CSV and JSON exports both list unique.txt.", async () =>
    {
        var csvPath = Path.Combine(testRoot, "scan.csv");
        var jsonPath = Path.Combine(testRoot, "scan.json");
        await exporter.ExportCsvAsync(result, csvPath);
        await exporter.ExportJsonAsync(result, jsonPath);
        Require(File.ReadAllText(csvPath).Contains("unique.txt", StringComparison.Ordinal), "CSV export is missing a file.");
        Require(File.ReadAllText(jsonPath).Contains("unique.txt", StringComparison.Ordinal), "JSON export is missing a file.");
    });
    Test("export_canceled_before_writing_original_file_unchanged", "A CSV export over an existing file that is canceled leaves the old file intact and no temporary file behind.", async () =>
    {
        var path = Path.Combine(testRoot, "existing.csv");
        await File.WriteAllTextAsync(path, "original content");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await RequireThrowsAsync<OperationCanceledException>(() => exporter.ExportCsvAsync(result, path, new ExportOptions(), canceled.Token));
        Require(File.ReadAllText(path) == "original content", "The original file must be unchanged.");
        Require(!Directory.EnumerateFiles(testRoot, ".existing.csv.*.tmp").Any(), "No temporary file may be left behind.");
    });
    Test("depth_limited_csv_lists_only_top_folders", "A depth-1 folder-only CSV export lists the root and its two folders.", async () =>
    {
        var shallowCsvPath = Path.Combine(testRoot, "shallow.csv");
        var shallow = await exporter.ExportCsvAsync(result, shallowCsvPath, new ExportOptions { MaximumDepth = 1, IncludeFiles = false });
        Require(shallow.RowsWritten == 3 && File.ReadAllLines(shallowCsvPath).Length == 4, "Depth-1 folder export should list the root and its two folders.");
    });
    Test("excel_export_is_well_formed_and_complete", "The Excel export writes all seven rows into well-formed workbook parts.", async () =>
    {
        var xlsxPath = Path.Combine(testRoot, "scan.xlsx");
        var xlsx = await reports.ExportXlsxAsync(result, xlsxPath, new ExportOptions());
        Require(xlsx.RowsWritten == 7 && !xlsx.IsTruncated, $"Excel export should write 7 rows (root, 2 folders, 4 files), wrote {xlsx.RowsWritten}.");
        using var workbook = System.IO.Compression.ZipFile.OpenRead(xlsxPath);
        foreach (var part in new[] { "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" })
        {
            using var reader = new StreamReader(workbook.GetEntry(part)!.Open());
            var document = System.Xml.Linq.XDocument.Parse(await reader.ReadToEndAsync());
            Require(document.Root is not null, $"{part} must be well-formed XML.");
            if (part.EndsWith("sheet1.xml", StringComparison.Ordinal))
            {
                Require(document.ToString().Contains("unique.txt", StringComparison.Ordinal), "Excel export is missing a file.");
            }
        }
    });
    Test("excel_export_row_limit_truncates_and_says_so", "An Excel export limited to two rows writes two rows and reports truncation.", async () =>
    {
        var capped = await reports.ExportXlsxAsync(result, Path.Combine(testRoot, "capped.xlsx"), new ExportOptions { MaximumRows = 2 });
        Require(capped.RowsWritten == 2 && capped.IsTruncated, "A row limit must truncate the export and say so.");
    });
    Test("depth_limited_html_report_omits_deeper_files", "A depth-1 HTML report shows the top folders but not the files inside them.", async () =>
    {
        var htmlPath = Path.Combine(testRoot, "scan.html");
        var html = await reports.ExportHtmlAsync(result, htmlPath, new ExportOptions { MaximumDepth = 1 });
        var htmlText = await File.ReadAllTextAsync(htmlPath);
        Require(html.RowsWritten == 3 && htmlText.Contains("Second", StringComparison.Ordinal) && !htmlText.Contains("unique.txt", StringComparison.Ordinal), "Depth-1 HTML report is wrong.");
    });
    TestSync("html_report_escapes_file_names", "A folder named <b>& is written escaped into the HTML report.", () =>
    {
        var escapedRoot = new ScanNode { Name = "<b>&", FullPath = "C:\\<b>&", IsDirectory = true };
        var (escapedHtml, _) = ReportExportService.BuildHtml(new ScanResult(escapedRoot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], []), new ExportOptions(), new ReportText(), System.Globalization.CultureInfo.InvariantCulture);
        Require(!escapedHtml.Contains("<b>&", StringComparison.Ordinal) && escapedHtml.Contains("&lt;b&gt;&amp;", StringComparison.Ordinal), "HTML report must escape file names.");
    });
    Test("json_export_handles_deeply_nested_folders", "A tree 60 folders deep exports to JSON without hitting the serializer's depth limit.", async () =>
    {
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
        Require(File.ReadAllText(deepJsonPath).Contains("\"d59\"", StringComparison.Ordinal), "JSON export must handle deeply nested folders.");
    });

    Test("atomic_write_failing_midway_keeps_original_file", "A write that fails after half of the data keeps the old file, and its temporary file is hidden while written and removed afterwards.", async () =>
    {
        var path = Path.Combine(testRoot, "midway.txt");
        await File.WriteAllTextAsync(path, "original content");
        await RequireThrowsAsync<IOException>(() => AtomicFile.WriteAsync(path, async stream =>
        {
            await stream.WriteAsync(new byte[4096]);
            var temporary = Directory.EnumerateFiles(testRoot, ".midway.txt.*.tmp").Single();
            Require(File.GetAttributes(temporary).HasFlag(FileAttributes.Hidden), "The temporary file must be hidden while it is written.");
            throw new IOException("The disk is full.");
        }));
        Require(File.ReadAllText(path) == "original content", "The original file must be unchanged.");
        Require(!Directory.EnumerateFiles(testRoot, ".midway.txt.*.tmp").Any(), "No temporary file may be left behind.");
    });
    TestSync("finished_atomic_write_is_complete_and_visible", "A settings file written atomically has the new content and is not hidden.", () =>
    {
        var path = Path.Combine(testRoot, "settings.json");
        File.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");
        Require(File.ReadAllText(path) == "new" && !File.GetAttributes(path).HasFlag(FileAttributes.Hidden), "The written file must be complete and visible.");
    });
    TestSync("stale_temporary_files_are_swept_after_an_hour", "Temporary files left by a killed write are removed once older than an hour, while fresh ones and other files stay.", () =>
    {
        var folder = Directory.CreateDirectory(Path.Combine(testRoot, "Sweep")).FullName;
        var stale = Path.Combine(folder, $".columns.json.{Guid.NewGuid():N}.tmp");
        var fresh = Path.Combine(folder, $".columns.json.{Guid.NewGuid():N}.tmp");
        var other = Path.Combine(folder, ".notes.tmp");
        foreach (var file in new[] { stale, fresh, other })
        {
            File.WriteAllText(file, "x");
        }
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));
        AtomicFile.SweepStale(folder);
        Require(!File.Exists(stale) && File.Exists(fresh) && File.Exists(other), "Only the stale temporary file may be removed.");
    });

    // ---- Copy and move ---------------------------------------------------------------------------------

    Test("copy_keeps_source_and_creates_target", "Copying unique.txt leaves the source and creates the copy.", async () =>
    {
        var copyTarget = Directory.CreateDirectory(Path.Combine(testRoot, "CopyTarget")).FullName;
        var copied = await fileOperations.CopyAsync([Path.Combine(secondDirectory.FullName, "unique.txt")], copyTarget, nint.Zero);
        Require(copied.Succeeded && File.Exists(Path.Combine(copyTarget, "unique.txt")) && File.Exists(Path.Combine(secondDirectory.FullName, "unique.txt")), "Copy must leave the source and create the target.");
    });
    Test("move_removes_source", "Moving the copy puts it in the target and removes it from its old place.", async () =>
    {
        var copyTarget = Path.Combine(testRoot, "CopyTarget");
        var moveTarget = Directory.CreateDirectory(Path.Combine(testRoot, "MoveTarget")).FullName;
        var moved = await fileOperations.MoveAsync([Path.Combine(copyTarget, "unique.txt")], moveTarget, nint.Zero);
        Require(moved.Succeeded && File.Exists(Path.Combine(moveTarget, "unique.txt")) && !File.Exists(Path.Combine(copyTarget, "unique.txt")), "Move must remove the source.");
    });

    // ---- Hard links ------------------------------------------------------------------------------------

    var linkRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Links")).FullName;
    var linkSource = Path.Combine(Directory.CreateDirectory(Path.Combine(linkRoot, "src")).FullName, "data.bin");
    await File.WriteAllBytesAsync(linkSource, new byte[1 << 20]);
    for (var link = 0; link < 8; link++)
    {
        var linkFolder = Directory.CreateDirectory(Path.Combine(linkRoot, $"link{link}")).FullName;
        Require(NativeTestMethods.CreateHardLink(Path.Combine(linkFolder, "data.bin"), linkSource, IntPtr.Zero), "Could not create the hard-link fan-out fixture.");
    }
    var linkResult = await scanner.ScanAsync(linkRoot);
    var notOwner = linkResult.Root.Files().First(static file => file.IsAdditionalHardLink);
    var ownerPath = Path.Combine(linkRoot, "link0", "data.bin");

    Test("hard_link_owner_is_stable_across_parallel_scans", "In 25 parallel scans of nine links to one file, the same path always carries the data and no snapshot changes appear.", async () =>
    {
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanSnapshot? previous = null;
        for (var run = 0; run < 25; run++)
        {
            var scan = await scanner.ScanAsync(linkRoot, new ScanOptions { Parallelism = 8 });
            owners.Add(scan.Root.Files().Single(static file => !file.IsAdditionalHardLink).FullPath);
            Require(scan.Root.AllocatedSize == scan.Root.Files().Max(static file => file.PhysicalAllocatedSize), "Shared data must be counted exactly once.");
            Require(scan.Root.Files().All(static file => file.SharesStorage && file.HardLinkCount == 0), "Every link must be marked as shared, with an unknown link count.");
            var snapshot = snapshotService.Create(scan);
            Require(previous is null || snapshotService.Compare(previous, snapshot).Count == 0, "Unchanged hard links must not produce snapshot changes.");
            previous = snapshot;
        }
        Require(owners.SequenceEqual([ownerPath]), $"The first path in sort order must always carry shared data; saw {string.Join(", ", owners)}.");
    });
    TestSync("filtered_view_counts_shared_data_once", "Filtering to all links of one file still counts its data exactly once.", () =>
        Require(ScanTreeFilter.Apply(linkResult, new TreeFilter { NamePattern = "data.bin" }, DateTimeOffset.UtcNow).Root.AllocatedSize == notOwner.PhysicalAllocatedSize,
            "Filtering all links must still count the shared data once."));
    TestSync("filtered_view_of_one_link_shows_its_data", "A filtered view holding only a link that the full scan counted as zero shows the space its data uses.", () =>
    {
        var single = ScanTreeFilter.Apply(linkResult with { Root = linkResult.Root.Children.Single(child => child.Name == Path.GetFileName(Path.GetDirectoryName(notOwner.FullPath))) },
            new TreeFilter { NamePattern = "data.bin" }, DateTimeOffset.UtcNow);
        Require(single.Root.FileCount == 1 && single.Root.AllocatedSize == notOwner.PhysicalAllocatedSize && single.Root.AllocatedSize > 0, "The single link must show its data.");
    });
    Test("link_counts_are_unknown_unless_read_precisely", "Normal scans report link counts as unknown (0), and precise scans read the real counts (1 and 9).", async () =>
    {
        var plain = await scanner.ScanAsync(secondDirectory.FullName);
        Require(plain.Root.Files().Single(static file => file.Name == "unique.txt").HardLinkCount == 0, "Directory enumeration must report link counts as unknown.");
        var precise = await scanner.ScanAsync(secondDirectory.FullName, new ScanOptions { PreciseAllocationSize = true });
        Require(precise.Root.Files().Single(static file => file.Name == "unique.txt").HardLinkCount == 1, "Precise mode must read the real link count.");
        var preciseLinks = await scanner.ScanAsync(linkRoot, new ScanOptions { PreciseAllocationSize = true });
        Require(preciseLinks.Root.Files().All(static file => file.HardLinkCount == 9 && file.SharesStorage), "Precise mode must report the real link count of hard links.");
    });

    // ---- Applying deletions to the scan ---------------------------------------------------------------

    TestSync("removing_items_updates_totals_attribution_and_statistics", "Removing the link that carries the data moves the data to another link, and removing a folder updates totals, counts, statistics and snapshots.", () =>
    {
        var beforeSize = linkResult.Root.Size;
        var edited = ScanTreeEditor.RemovePaths(linkResult, [ownerPath], DateTimeOffset.UtcNow);
        Require(ScanTreeEditor.Find(edited.Root, ownerPath) is null && edited.Root.Size == beforeSize - (1 << 20) && edited.Root.FileCount == 8, "Removing a path must update every total above it.");
        Require(edited.Root.AllocatedSize == notOwner.PhysicalAllocatedSize && edited.Root.Files().Count(static file => !file.IsAdditionalHardLink) == 1, "Another remaining link must carry the shared data.");
        Require(edited.Root.Children.Single(static child => child.Name == "link0").Size == 0, "The emptied folder must show zero.");
        var folderGone = ScanTreeEditor.RemovePaths(edited, [Path.Combine(linkRoot, "src")], DateTimeOffset.UtcNow);
        Require(folderGone.Root.Size == beforeSize - (2 << 20) && folderGone.Root.FolderCount == 8 && folderGone.Extensions.Single().FileCount == 7, "Removing a folder must update totals, counts and statistics.");
        Require(!snapshotService.Create(folderGone).Entries.Any(static entry => entry.RelativePath.StartsWith("src", StringComparison.OrdinalIgnoreCase)), "Snapshots of an edited scan must not contain removed items.");
    });

    Test("failed_folder_delete_drops_exactly_the_removed_files", "When deleting a folder fails because one file is open, the scan drops the four files already deleted and keeps counting the open one.", async () =>
    {
        var partialRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Partial")).FullName;
        var victim = Directory.CreateDirectory(Path.Combine(partialRoot, "Victim")).FullName;
        foreach (var index in Enumerable.Range(1, 5))
        {
            await File.WriteAllBytesAsync(Path.Combine(victim, $"f{index}.bin"), new byte[1000 * index]);
        }
        var partialScan = await scanner.ScanAsync(partialRoot);
        using (new FileStream(Path.Combine(victim, "f3.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await RequireThrowsAsync<IOException>(() => fileOperations.DeletePermanentlyAsync(victim));
        }
        var vanished = ScanTreeEditor.FindVanished(partialScan.Root, victim);
        Require(vanished.Count == 4 && !vanished.Any(static path => path.EndsWith("f3.bin", StringComparison.OrdinalIgnoreCase)), $"Expected the four deleted files, found {vanished.Count}.");
        var updated = ScanTreeEditor.RemovePaths(partialScan, vanished, DateTimeOffset.UtcNow);
        Require(updated.Root.FileCount == 1 && updated.Root.Size == 3000, "The scan must keep only the file that is still there.");
    });

    // ---- Updates -------------------------------------------------------------------------------------------

    Test("empty_update_configuration_stays_disabled", "Without a repository or feed, the update check is neither configured nor offering anything.", async () =>
    {
        var check = await new UpdateService().CheckAsync(new UpdateConfiguration(), new Version(0, 1, 0));
        Require(!check.IsConfigured && !check.IsUpdateAvailable, "An empty update configuration must stay disabled.");
    });
    Test("update_configuration_loads_camel_case_json", "update-config.json with camel-case names loads the GitHub repository.", async () =>
    {
        var path = Path.Combine(testRoot, "update-config.json");
        await File.WriteAllTextAsync(path, """{"githubRepository":"Naxterra/DiskLoom","checkOnStartup":true,"checkIntervalHours":24}""");
        Require((await UpdateService.LoadConfigurationAsync(path)).GitHubRepository == "Naxterra/DiskLoom", "Camel-case GitHub update configuration was not loaded.");
    });

    const string githubRelease = """
        {
          "tag_name": "v0.2.0",
          "body": "GitHub release notes",
          "html_url": "https://github.com/Naxterra/DiskLoom/releases/tag/v0.2.0",
          "published_at": "2026-08-18T08:00:00Z",
          "assets": [
            { "name": "DiskLoom-Setup-x64-de-DE.msi", "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64-de-DE.msi", "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            { "name": "DiskLoom-0.2.0-win-x64-portable.zip", "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-0.2.0-win-x64-portable.zip", "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
            { "name": "DiskLoom-Setup-x64.msi", "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64.msi", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
          ]
        }
        """;
    using var githubClient = new HttpClient(new StubHttpMessageHandler(request =>
    {
        Require(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/Naxterra/DiskLoom/releases/latest", "GitHub update endpoint is incorrect.");
        Require(request.Headers.UserAgent.ToString().StartsWith("DiskLoom/", StringComparison.Ordinal), "GitHub request is missing its user agent.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(githubRelease, Encoding.UTF8, "application/json") };
    }));
    var repositoryConfiguration = new UpdateConfiguration { GitHubRepository = "Naxterra/DiskLoom" };

    Test("github_release_is_detected_with_english_msi_by_default", "A newer GitHub release is offered with the English MSI, its digest and the release page.", async () =>
    {
        var check = await new UpdateService(githubClient).CheckAsync(repositoryConfiguration, new Version(0, 1, 4));
        Require(check.IsConfigured && check.IsUpdateAvailable && check.Manifest?.HasVerifiableInstaller == true, "The release must be offered with a verifiable installer.");
        Require(check.Manifest!.InstallerUrl.EndsWith("/DiskLoom-Setup-x64.msi", StringComparison.Ordinal) && check.Manifest.Sha256.StartsWith('a'), "Without a language preference the English MSI must be chosen.");
        Require(check.Manifest.ReleaseNotesUrl.EndsWith("/v0.2.0", StringComparison.Ordinal), "The GitHub release page was not retained.");
    });
    Test("german_ui_updates_with_german_msi", "With the German UI, the German MSI and its digest are chosen.", async () =>
    {
        var check = await new UpdateService(githubClient).CheckAsync(repositoryConfiguration, new Version(0, 1, 4), preferredLanguage: "de-DE");
        Require(check.Manifest!.InstallerUrl.EndsWith("/DiskLoom-Setup-x64-de-DE.msi", StringComparison.Ordinal) && check.Manifest.Sha256.StartsWith('b'), "The German UI must update with the German MSI.");
    });
    Test("only_repository_msis_with_digest_install_directly", "Only an MSI from the configured repository's releases with a digest installs directly, with or without a pinned certificate.", async () =>
    {
        var manifest = (await new UpdateService(githubClient).CheckAsync(repositoryConfiguration, new Version(0, 1, 4))).Manifest!;
        var pinned = repositoryConfiguration with { PublisherCertificateSha256 = new string('A', 64) };
        Require(UpdateService.CanInstallDirectly(manifest, repositoryConfiguration) && UpdateService.CanInstallDirectly(manifest, pinned), "A repository MSI must be installable.");
        var foreign = manifest with { InstallerUrl = "https://example.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64.msi" };
        Require(!UpdateService.CanInstallDirectly(foreign, repositoryConfiguration) && !UpdateService.CanInstallDirectly(foreign, pinned), "A foreign host must never install directly, not even with a pinned certificate.");
        var exe = manifest with { InstallerUrl = "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64.exe" };
        Require(!UpdateService.CanInstallDirectly(exe, pinned), "An EXE skips the package checks and must not install directly.");
        Require(!UpdateService.CanInstallDirectly(manifest, new UpdateConfiguration { GitHubRepository = "Someone/Else" }), "Another repository's download must not install directly.");
        Require(!UpdateService.CanInstallDirectly(manifest with { Sha256 = string.Empty }, repositoryConfiguration), "An installer without a digest must not install directly.");
    });
    Test("stale_stable_json_from_older_release_is_ignored", "A stable.json whose version differs from the release tag is ignored, and GitHub's MSI digest is used instead.", async () =>
    {
        const string releaseWithManifest = """
            {
              "tag_name": "v0.2.0",
              "html_url": "https://github.com/Naxterra/DiskLoom/releases/tag/v0.2.0",
              "assets": [
                { "name": "stable.json", "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/stable.json" },
                { "name": "DiskLoom-Setup-x64.msi", "browser_download_url": "https://github.com/Naxterra/DiskLoom/releases/download/v0.2.0/DiskLoom-Setup-x64.msi", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
              ]
            }
            """;
        const string staleManifest = """{"version":"0.1.9","installerUrl":"https://github.com/Naxterra/DiskLoom/releases/download/v0.1.9/DiskLoom-Setup-x64.msi","sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}""";
        using var client = new HttpClient(new StubHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("stable.json", StringComparison.Ordinal) ? staleManifest : releaseWithManifest, Encoding.UTF8, "application/json")
        }));
        var check = await new UpdateService(client).CheckAsync(repositoryConfiguration, new Version(0, 1, 4));
        Require(check.Manifest!.Version == "0.2.0" && check.Manifest.Sha256.StartsWith('a') && check.Manifest.InstallerUrl.Contains("/v0.2.0/", StringComparison.Ordinal),
            "The stale stable.json must be ignored.");
    });
    Test("non_diskloom_file_is_rejected_as_update_package", "A file that is not DiskLoom's MSI fails the package check.", async () =>
    {
        var notAnMsi = Path.Combine(testRoot, "fake.msi");
        await File.WriteAllTextAsync(notAnMsi, "not a Windows Installer package");
        Require(!UpdateService.IsExpectedMsi(notAnMsi, "0.2.0", out _), "A file that is not DiskLoom's MSI must be rejected.");
    });
    Test("repository_without_releases_is_not_an_error", "A repository without releases is a successful check with no update.", async () =>
    {
        using var noReleaseClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var check = await new UpdateService(noReleaseClient).CheckAsync(repositoryConfiguration, new Version(0, 1, 4));
        Require(check.IsConfigured && !check.IsUpdateAvailable, "A repository without releases should be a successful no-update result.");
    });
    TestSync("update_cache_keeps_only_a_recent_newer_installer", "Cleaning the update cache removes installers that have run, partial downloads, logs of failed installs and newer installers older than a day.", () =>
    {
        var cache = Directory.CreateDirectory(Path.Combine(testRoot, "Updates")).FullName;
        void Put(string version, string name, DateTime? written = null)
        {
            var file = Path.Combine(Directory.CreateDirectory(Path.Combine(cache, version)).FullName, name);
            File.WriteAllText(file, "installer");
            if (written is { } time)
            {
                File.SetLastWriteTimeUtc(file, time);
            }
        }
        Put("0.1.20", "DiskLoom-0.1.20.msi");
        Put("0.1.25", "DiskLoom-0.1.25.msi");
        Put("0.1.30", "DiskLoom-0.1.30.msi");
        Put("0.1.30", "DiskLoom-0.1.30.download.msi");
        Put("0.1.31", "install.log");
        Put("0.1.32", "DiskLoom-0.1.32.msi", DateTime.UtcNow.AddDays(-2));
        UpdateService.CleanUpdateCache(new Version(0, 1, 25), cache);
        Require(Directory.EnumerateDirectories(cache).Select(Path.GetFileName).SequenceEqual(["0.1.30"]), "Only the recent newer version may remain.");
        Require(Directory.EnumerateFiles(Path.Combine(cache, "0.1.30")).Select(Path.GetFileName).SequenceEqual(["DiskLoom-0.1.30.msi"]), "The partial download must be removed.");
    });

    // Two identical files in their own folder, scanned and checked, as the duplicate list would show them.
    async Task<DuplicateGroup> DuplicatePairAsync(string folderName)
    {
        var folder = Directory.CreateDirectory(Path.Combine(testRoot, folderName)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "a.bin"), duplicateBytes);
        await File.WriteAllBytesAsync(Path.Combine(folder, "b.bin"), duplicateBytes);
        var found = await new DuplicateFinder().FindAsync((await scanner.ScanAsync(folder)).Root, minimumFileSize: 1);
        Require(found.Groups.Count == 1 && found.Groups[0].Files.Count == 2, "The duplicate fixture must form one group of two.");
        return found.Groups[0];
    }

    // ---- Run --------------------------------------------------------------------------------------------------

    var failed = 0;
    foreach (var (name, scenario, body) in tests)
    {
        try
        {
            await body();
            Console.WriteLine($"Program.cs::{name} | {scenario} | PASS");
        }
        catch (Exception exception)
        {
            failed++;
            Console.WriteLine($"Program.cs::{name} | {scenario} | FAIL: {exception.Message}");
        }
    }
    Console.WriteLine(failed == 0 ? "DiskLoom core smoke tests passed." : $"DiskLoom core smoke tests: {failed} failed.");
    return failed == 0 ? 0 : 1;
}
finally
{
    if (Directory.Exists(WindowsPaths.ToExtended(testRoot)))
    {
        Directory.Delete(WindowsPaths.ToExtended(testRoot), recursive: true);
    }
    var parent = Path.GetDirectoryName(testRoot)!;
    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
    {
        Directory.Delete(parent);
    }
}

static void CreateJunction(string link, string target)
{
    using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true
    })!;
    mklink.StandardOutput.ReadToEnd();
    mklink.WaitForExit();
    Require(mklink.ExitCode == 0 && Directory.Exists(link), $"Could not create the junction {link}.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task RequireThrowsAsync<TException>(Func<Task> action) where TException : Exception
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
