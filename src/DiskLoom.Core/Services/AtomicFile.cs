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
                result = await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
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
            File.WriteAllText(temporary, contents);
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    // Hidden-looking name in the same folder, so the final move is a rename on the same volume.
    public static string TemporaryPathFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

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
