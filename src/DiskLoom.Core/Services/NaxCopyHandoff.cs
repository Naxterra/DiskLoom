using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DiskLoom.Core.Services;

public enum NaxCopyOperation { Copy = 0, Move = 1 }

public enum NaxCopyHandoffResult { NotDelivered, Accepted, Unconfirmed }

/// <summary>
/// Hands copy and move jobs to Nax-Copy (the owner's replacement for Explorer's copy engine) when it is installed, over the
/// named pipe its Explorer extension uses, so DiskLoom's "Copy to…" and "Move to…" run like a paste in Explorer.
/// Protocol (Nax-Copy src/NaxCopy.Core/Handoff.cs): uint32 length, then "NXC1", uint32 mode (0 copy, 1 move),
/// destination, uint32 count, sources; strings are uint32 char count + UTF-16. Nax-Copy answers one byte, 1 = accepted.
/// Nax-Copy's own switch (HKCU\Software\NaxCopy\ExplorerIntegration) turns this off too.
/// </summary>
public static class NaxCopyHandoff
{
    private const uint Magic = 0x3143584E; // "NXC1"
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>NaxCopy.exe when Nax-Copy is installed and its takeover is switched on, else null.</summary>
    public static string? AppPath
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\NaxCopy");
            if (user?.GetValue("ExplorerIntegration") is int enabled && enabled == 0)
            {
                return null;
            }
            // The machine-wide registration is written by the installer and is not writable without elevation, so it
            // wins. The per-user path is only a fallback, and never in an elevated DiskLoom, where any program running
            // as the user could otherwise point it at something that then starts elevated.
            using (var machine = Registry.LocalMachine.OpenSubKey(@"Software\NaxCopy"))
            {
                if (machine?.GetValue("AppPath") is string machinePath && File.Exists(machinePath))
                {
                    return machinePath;
                }
            }
            if (Environment.IsPrivilegedProcess)
            {
                return null;
            }
            return user?.GetValue("AppPath") is string userPath && File.Exists(userPath) ? userPath : null;
        }
    }

    private static string PipeName => $"NaxCopy-{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Accepted when Nax-Copy took the job (it then runs it in its own window). NotDelivered when it isn't installed,
    /// is switched off, could not be reached or refused the job; the caller then uses Explorer's engine as before.
    /// Unconfirmed when the job was sent but no answer came back: Nax-Copy may be running it, so the caller must not
    /// start the same job a second time.
    /// </summary>
    public static async Task<NaxCopyHandoffResult> TrySendAsync(NaxCopyOperation operation, string destination, IReadOnlyList<string> sources, CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0 || AppPath is not { } app)
        {
            return NaxCopyHandoffResult.NotDelivered;
        }
        var sent = false;
        try
        {
            await using var pipe = await ConnectAsync(app, cancellationToken);
            if (pipe is null)
            {
                return NaxCopyHandoffResult.NotDelivered;
            }
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var server))
            {
                AllowSetForegroundWindow(server); // DiskLoom has the foreground; let Nax-Copy's window come to the front
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AckTimeout);
            var message = Serialize(operation, destination, sources);
            sent = true;
            await pipe.WriteAsync(message, timeout.Token);
            var ack = new byte[1];
            var answered = await pipe.ReadAsync(ack, timeout.Token) == 1;
            return !answered ? NaxCopyHandoffResult.Unconfirmed
                : ack[0] == 1 ? NaxCopyHandoffResult.Accepted
                : NaxCopyHandoffResult.NotDelivered;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return sent ? NaxCopyHandoffResult.Unconfirmed : NaxCopyHandoffResult.NotDelivered;
        }
    }

    /// <summary>Connects to the running Nax-Copy, or starts it (as its Explorer extension does) and waits for its pipe.</summary>
    private static async Task<NamedPipeClientStream?> ConnectAsync(string app, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        Process? started = null;
        try
        {
            while (true)
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(250, cancellationToken);
                    return pipe;
                }
                catch (TimeoutException)
                {
                    await pipe.DisposeAsync();
                }

                started ??= Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = false });
                if (DateTime.UtcNow >= deadline || started is null || started.HasExited)
                {
                    return null;
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        finally
        {
            started?.Dispose();
        }
    }

    internal static byte[] Serialize(NaxCopyOperation operation, string destination, IReadOnlyList<string> sources)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write(0u); // length, patched below
        writer.Write(Magic);
        writer.Write((uint)operation);
        WriteString(writer, destination);
        writer.Write((uint)sources.Count);
        foreach (var source in sources)
            WriteString(writer, source);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 4));
        return bytes;
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        writer.Write((uint)text.Length);
        writer.Write(Encoding.Unicode.GetBytes(text));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
