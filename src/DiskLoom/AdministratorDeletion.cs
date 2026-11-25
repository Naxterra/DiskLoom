using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskLoom.Core.Services;

namespace DiskLoom;

internal static class AdministratorDeletion
{
    internal const string Switch = "--confirm-delete-as-administrator";
    internal const string ManifestSwitch = "--manifest";
    internal const int Canceled = 2;
    private const int BatchSize = 25;
    private const uint OkCancel = 0x00000001;
    private const uint WarningIcon = 0x00000030;
    private const uint ErrorIcon = 0x00000010;
    private const uint DefaultButtonCancel = 0x00000100;
    private const uint SetForeground = 0x00010000;
    private const int Ok = 1;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;

    internal static async Task<int> RequestAsync(IReadOnlyList<string> paths, string language)
    {
        var manifestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskLoom");
        Directory.CreateDirectory(manifestDirectory);
        var manifestPath = Path.Combine(manifestDirectory, $"admin-delete-{Guid.NewGuid():N}.json");
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths));
        await File.WriteAllBytesAsync(manifestPath, payload);
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));

        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException())
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(Switch);
            start.ArgumentList.Add(language);
            start.ArgumentList.Add(ManifestSwitch);
            start.ArgumentList.Add(manifestPath);
            start.ArgumentList.Add(sha256);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("DiskLoom could not start the deletion process.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            try
            {
                File.Delete(manifestPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Ignore temporary manifest cleanup failure.
            }
        }
    }

    internal static bool CanRetryPath(string path)
    {
        // Only unambiguous absolute paths on local drive letters can cross the privilege boundary.
        if (path.Length <= 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }
        var segments = Path.TrimEndingDirectorySeparator(path)[3..].Split('\\');
        var invalid = Path.GetInvalidFileNameChars();
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".." && segment.IndexOfAny(invalid) < 0);
    }

    internal static int Run(string[] args)
    {
        if (!Environment.IsPrivilegedProcess || args.Length < 2)
        {
            return 1;
        }

        IReadOnlyList<string>? paths = TryReadPaths(args);
        if (paths is null || paths.Count == 0 || !paths.All(CanRetryPath))
        {
            return 1;
        }

        var german = args[0].Equals("de-DE", StringComparison.OrdinalIgnoreCase);
        var service = new FileOperationService();
        var incomplete = false;

        foreach (var batch in paths.Chunk(BatchSize))
        {
            // Confirm inside the elevated process. An external caller can supply arguments,
            // and the UAC window only identifies the program, not the files or folders it will delete.
            string prompt;
            if (batch.Length == 1)
            {
                var single = batch[0];
                var folder = Directory.Exists(WindowsPaths.ToExtended(single));
                prompt = german
                    ? $"{(folder ? "Diesen Ordner samt Inhalt dauerhaft löschen?" : "Diese Datei dauerhaft löschen?")}\n\n{single}\n\nDies kann nicht rückgängig gemacht werden."
                    : $"{(folder ? "Permanently delete this folder and all its contents?" : "Permanently delete this file?")}\n\n{single}\n\nThis cannot be undone.";
            }
            else
            {
                var listing = string.Join(Environment.NewLine, batch);
                prompt = german
                    ? $"{batch.Length} Elemente dauerhaft löschen?\n\n{listing}\n\nDies kann nicht rückgängig gemacht werden."
                    : $"Permanently delete {batch.Length} items?\n\n{listing}\n\nThis cannot be undone.";
            }

            var answer = MessageBox(0, prompt, "DiskLoom", OkCancel | WarningIcon | DefaultButtonCancel | SetForeground);
            if (answer != Ok)
            {
                return Canceled;
            }

            foreach (var path in batch)
            {
                try
                {
                    RefuseRemoteOrLinkedAncestors(path);
                    service.DeletePermanentlyAsync(path).GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // The item was already removed, possibly as part of another selected folder.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    incomplete = true;
                    _ = MessageBox(0, $"{path}\n\n{exception.Message}", "DiskLoom", ErrorIcon | SetForeground);
                }
            }
        }
        return incomplete ? 1 : 0;
    }

    private static IReadOnlyList<string>? TryReadPaths(string[] args)
    {
        if (args.Length == 4 && args[1] == ManifestSwitch)
        {
            var manifestPath = args[2];
            var expectedHex = args[3];
            if (expectedHex.Length != 64 || !expectedHex.All(Uri.IsHexDigit))
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actualHash = SHA256.HashData(stream);
                var expectedHash = Convert.FromHexString(expectedHex);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    return null;
                }
                stream.Position = 0;
                return JsonSerializer.Deserialize<string[]>(stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                return null;
            }
        }

        return args[1..];
    }

    private static void RefuseRemoteOrLinkedAncestors(string path)
    {
        var fullPath = ScanPaths.Normalize(path).TrimEnd('\\', '/');
        var volume = GetVolumeRootWithTrailingSeparator(fullPath);
        var driveType = GetDriveType(volume);
        if (driveType is not (DriveFixed or DriveRemovable))
        {
            throw new IOException("Administrator deletion requires a local disk.");
        }

        var chain = ScanPaths.GetChain(fullPath);
        for (var index = 0; index < chain.Count - 1; index++)
        {
            var parent = chain[index];
            var extendedParent = WindowsPaths.ToExtended(parent);
            var attributes = File.GetAttributes(extendedParent);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            if (FileSystemScanner.IsCloudPlaceholder(FileSystemScanner.GetReparsePointTag(extendedParent)))
            {
                continue;
            }

            if (WindowsPaths.IsVolumeRoot(parent))
            {
                var mountedVolume = parent.TrimEnd('\\', '/') + "\\";
                if (GetDriveType(mountedVolume) is DriveFixed or DriveRemovable)
                {
                    continue;
                }
            }

            throw new IOException($"Administrator deletion cannot follow this folder link: {parent}");
        }
    }

    private static string GetVolumeRootWithTrailingSeparator(string path)
    {
        var buffer = new char[Math.Max(1024, path.Length + 16)];
        var volume = GetVolumePathName(WindowsPaths.ToExtended(path), buffer, (uint)buffer.Length) ||
                     GetVolumePathName(path, buffer, (uint)buffer.Length)
            ? new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0')))
            : path[..3];
        var stripped = WindowsPaths.StripExtendedPrefix(volume).TrimEnd('\\', '/');
        return stripped + "\\";
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint flags);
}
