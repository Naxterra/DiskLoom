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
