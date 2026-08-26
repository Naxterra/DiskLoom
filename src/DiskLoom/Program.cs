using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DiskLoom;

public static class Program
{
    private const string InstanceKey = "Naxterra.DiskLoom.Main";

    internal static event EventHandler<AppActivationArguments>? RedirectedActivation;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static bool RedirectToExistingInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primaryInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (primaryInstance.IsCurrent)
        {
            primaryInstance.Activated += OnRedirectedActivation;
            return false;
        }

        RedirectActivationTo(activation, primaryInstance);
        return true;
    }

    private static void OnRedirectedActivation(object? sender, AppActivationArguments args) =>
        RedirectedActivation?.Invoke(sender, args);

    internal static void ReleaseInstanceKeyForRestart() => AppInstance.GetCurrent().UnregisterKey();

    internal static void ReclaimInstanceKeyAfterFailedRestart() => AppInstance.FindOrRegisterForKey(InstanceKey);

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance primaryInstance)
    {
        var completionEvent = CreateEvent(nint.Zero, true, false, null);
        if (completionEvent == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Exception? redirectException = null;
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await primaryInstance.RedirectActivationToAsync(args);
                }
                catch (Exception exception)
                {
                    redirectException = exception;
                }
                finally
                {
                    SetEvent(completionEvent);
                }
            });

            _ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, [completionEvent], out _);
            if (redirectException is not null)
            {
                throw redirectException;
            }

            using var process = Process.GetProcessById((int)primaryInstance.ProcessId);
            if (process.MainWindowHandle != nint.Zero)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        finally
        {
            CloseHandle(completionEvent);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEvent(nint eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(nint handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint flags,
        uint milliseconds,
        ulong handleCount,
        nint[] handles,
        out uint index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
