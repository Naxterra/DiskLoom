using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public sealed class UpdateService(HttpClient? httpClient = null)
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<UpdateConfiguration> LoadConfigurationAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new UpdateConfiguration();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UpdateConfiguration>(stream, ConfigurationJsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new UpdateConfiguration();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateConfiguration configuration,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(configuration.GitHubRepository))
        {
            return await CheckGitHubReleaseAsync(configuration.GitHubRepository, currentVersion, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(configuration.ManifestUrl))
        {
            return new UpdateCheckResult(false, false, currentVersion, null, "No update feed is configured for this build.");
        }

        if (!Uri.TryCreate(configuration.ManifestUrl, UriKind.Absolute, out var manifestUri) ||
            (manifestUri.Scheme != Uri.UriSchemeHttps && !manifestUri.IsLoopback))
        {
            return new UpdateCheckResult(false, false, currentVersion, null, "The update feed must use HTTPS.");
        }

        var manifest = await _httpClient.GetFromJsonAsync<UpdateManifest>(manifestUri, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The update feed returned an empty manifest.");
        if (!Version.TryParse(manifest.Version, out var availableVersion))
        {
            throw new InvalidDataException($"The update manifest contains an invalid version: {manifest.Version}");
        }

        return new UpdateCheckResult(
            true,
            availableVersion > currentVersion,
            currentVersion,
            manifest,
            availableVersion > currentVersion ? $"DiskLoom {availableVersion} is available." : "DiskLoom is up to date.");
    }

    private async Task<UpdateCheckResult> CheckGitHubReleaseAsync(
        string repository,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(static part => part.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))))
        {
            return new UpdateCheckResult(false, false, currentVersion, null, "The GitHub repository must use owner/name format.");
        }

        var endpoint = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd($"DiskLoom/{currentVersion}");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(true, false, currentVersion, null, "No GitHub release has been published yet.");
        }
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
        if (!TryParseReleaseVersion(release.TagName, out var availableVersion))
        {
            throw new InvalidDataException($"The latest GitHub release tag is not a version: {release.TagName}");
        }

        var manifestAsset = release.Assets.FirstOrDefault(static asset =>
            asset.Name.Equals("stable.json", StringComparison.OrdinalIgnoreCase));
        UpdateManifest manifest;
        if (manifestAsset is not null && Uri.TryCreate(manifestAsset.BrowserDownloadUrl, UriKind.Absolute, out var manifestUri))
        {
            manifest = await _httpClient.GetFromJsonAsync<UpdateManifest>(manifestUri, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The stable.json GitHub release asset is empty.");
            manifest = manifest with
            {
                ReleaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? release.Body ?? string.Empty : manifest.ReleaseNotes,
                ReleaseNotesUrl = string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl) ? release.HtmlUrl : manifest.ReleaseNotesUrl
            };
        }
        else
        {
            var installer = release.Assets
                .Where(static asset => Path.GetExtension(asset.Name).Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                                       Path.GetExtension(asset.Name).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static asset => asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            var digest = installer?.Digest;
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..]
                : string.Empty;
            manifest = new UpdateManifest
            {
                Version = availableVersion.Build >= 0 ? availableVersion.ToString(3) : availableVersion.ToString(2),
                InstallerUrl = installer?.BrowserDownloadUrl ?? string.Empty,
                Sha256 = sha256,
                ReleaseNotes = release.Body ?? string.Empty,
                ReleaseNotesUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                    ? $"https://github.com/{repository}/releases/latest"
                    : release.HtmlUrl,
                PublishedUtc = release.PublishedUtc,
                Mandatory = false
            };
        }

        var isAvailable = availableVersion > currentVersion;
        return new UpdateCheckResult(
            true,
            isAvailable,
            currentVersion,
            manifest,
            isAvailable ? $"DiskLoom {availableVersion} is available on GitHub." : "DiskLoom is up to date with GitHub Releases.");
    }

    private static bool TryParseReleaseVersion(string tag, out Version version)
    {
        var candidate = tag.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
        {
            candidate = candidate[1..];
        }
        var suffix = candidate.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            candidate = candidate[..suffix];
        }
        return Version.TryParse(candidate, out version!);
    }

    public async Task<string> DownloadAndVerifyAsync(
        UpdateManifest manifest,
        UpdateConfiguration configuration,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.PublisherCertificateSha256))
        {
            throw new InvalidOperationException("This build has no pinned publisher certificate and will not install remote updates.");
        }

        var installerUri = new Uri(manifest.InstallerUrl, UriKind.Absolute);
        if (installerUri.Scheme != Uri.UriSchemeHttps && !installerUri.IsLoopback)
        {
            throw new InvalidDataException("The installer URL must use HTTPS.");
        }

        var extension = Path.GetExtension(installerUri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".msi" or ".exe"))
        {
            throw new InvalidDataException("The update must be an MSI or EXE installer.");
        }

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskLoom",
            "Updates",
            manifest.Version);
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, $"DiskLoom-{manifest.Version}{extension}");
        var temporary = Path.Combine(updateDirectory, $"DiskLoom-{manifest.Version}.download{extension}");

        using var response = await _httpClient.GetAsync(installerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }

        await using (var file = File.OpenRead(temporary))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(NormalizeFingerprint(manifest.Sha256))))
            {
                File.Delete(temporary);
                throw new CryptographicException("The downloaded installer does not match the release manifest SHA-256 hash.");
            }
        }

        if (!AuthenticodeVerifier.IsTrustedAndSignedBy(temporary, configuration.PublisherCertificateSha256, out var signatureError))
        {
            File.Delete(temporary);
            throw new CryptographicException(signatureError);
        }

        File.Move(temporary, destination, overwrite: true);
        return destination;
    }

    public Process StartInstaller(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\" /passive")
            {
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows Installer could not be started.");
        }

        return Process.Start(new ProcessStartInfo(installerPath, "/update")
        {
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("The update installer could not be started.");
    }

    private static string NormalizeFingerprint(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static class AuthenticodeVerifier
    {
        private static readonly Guid GenericVerifyAction = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        public static bool IsTrustedAndSignedBy(string filePath, string expectedCertificateSha256, out string error)
        {
            if (!OperatingSystem.IsWindows())
            {
                error = "Authenticode verification is only available on Windows.";
                return false;
            }

            var fileInfo = new WinTrustFileInfo(filePath);
            var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
                var trustData = new WinTrustData(fileInfoPointer);
                var action = GenericVerifyAction;
                var status = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
                if (status != 0)
                {
                    error = $"Windows rejected the installer's Authenticode signature (0x{status:X8}).";
                    return false;
                }

#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
                var actual = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                if (!actual.Equals(NormalizeFingerprint(expectedCertificateSha256), StringComparison.OrdinalIgnoreCase))
                {
                    error = "The installer is signed, but not by the publisher certificate pinned in this DiskLoom build.";
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (CryptographicException exception)
            {
                error = $"The installer signature could not be read: {exception.Message}";
                return false;
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr windowHandle, [In] ref Guid actionId, [In] ref WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo(string filePath)
        {
            public uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath = filePath;
            public IntPtr FileHandle = IntPtr.Zero;
            public IntPtr KnownSubject = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData(IntPtr fileInfo)
        {
            public uint StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            public IntPtr PolicyCallbackData = IntPtr.Zero;
            public IntPtr SipClientData = IntPtr.Zero;
            public uint UiChoice = 2;
            public uint RevocationChecks = 0;
            public uint UnionChoice = 1;
            public IntPtr FileInfo = fileInfo;
            public uint StateAction = 0;
            public IntPtr StateData = IntPtr.Zero;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference = null;
            public uint ProviderFlags = 0;
            public uint UiContext = 0;
            public IntPtr SignatureSettings = IntPtr.Zero;
        }
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedUtc { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubReleaseAsset> Assets { get; init; } = [];
    }

    private sealed record GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
