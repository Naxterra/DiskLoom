namespace DiskLoom.Core.Services;

// Writes to a temporary file next to the target and moves it over the target only when it is complete. A failure,
// a full disk or a cancellation therefore leaves the previous file untouched and no half-written file behind.
public static class AtomicFile
{
    public static async Task<T> WriteAsync<T>(string path, Func<Stream, Task<T>> write, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var temporary = TemporaryPathFor(target);
        try
        {
            T result;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                File.SetAttributes(temporary, FileAttributes.Hidden);
                result = await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // The data must be on the disk before the rename, or a power loss could leave an empty target.
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Replace(temporary, target);
            return result;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static Task WriteAsync(string path, Func<Stream, Task> write, CancellationToken cancellationToken = default) =>
        WriteAsync<bool>(path, async stream =>
        {
            await write(stream).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public static void WriteAllText(string path, string contents)
    {
        var target = Path.GetFullPath(path);
        var temporary = TemporaryPathFor(target);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetAttributes(temporary, FileAttributes.Hidden);
                using var writer = new StreamWriter(stream);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            Replace(temporary, target);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    // A hidden file in the same folder, so the final move is a rename on the same volume. It is only visible to
    // the user if the process is killed while writing, and SweepStale removes such leftovers.
    public static string TemporaryPathFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

    // Moves a finished temporary file over the target. The rename keeps the attributes of the moved file, so the
    // Hidden attribute is removed first.
    public static void Replace(string temporary, string target)
    {
        File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.Hidden);
        File.Move(temporary, target, overwrite: true);
    }

    // Deletes temporary files that a killed process left in this folder, once they are older than an hour, so a
    // write that is still running elsewhere is never disturbed.
    public static void SweepStale(string folder, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var file in Directory.EnumerateFiles(folder, ".*.tmp"))
        {
            if (TemporaryName.IsMatch(Path.GetFileName(file)) && now - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
            {
                TryDelete(file);
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TemporaryName =
        new(@"^\..+\.[0-9a-f]{32}\.tmp$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original target is intact; a leftover temporary file is the lesser problem.
        }
    }
}
