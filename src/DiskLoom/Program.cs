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
    private const string SingleInstanceMutexName = "Local\\Naxterra.DiskLoom.SingleInstance";
    private static Mutex? _singleInstanceMutex;

    internal static event EventHandler<AppActivationArguments>? RedirectedActivation;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToExistingInstance())
        {
            return 0;
        }

        if (!TryAcquireSingleInstanceMutex())
        {
            AppInstance.GetCurrent().UnregisterKey();
            ActivateExistingWindow();
            return 0;
        }

        try
        {
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }
        finally
        {
            ReleaseSingleInstanceMutex();
        }
    }

    private static bool TryAcquireSingleInstanceMutex()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            return _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void ReleaseSingleInstanceMutex()
    {
        if (_singleInstanceMutex is null)
        {
            return;
        }

        try
        {
            _singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // This process did not own the mutex.
        }
        finally
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private static void ActivateExistingWindow()
    {
        var currentProcessId = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("DiskLoom"))
        {
            using (process)
            {
                if (process.Id != currentProcessId && process.MainWindowHandle != nint.Zero)
                {
                    SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
            }
        }
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

    internal static void ReleaseInstanceKeyForRestart()
    {
        AppInstance.GetCurrent().UnregisterKey();
        ReleaseSingleInstanceMutex();
    }

    internal static void ReclaimInstanceKeyAfterFailedRestart()
    {
        AppInstance.FindOrRegisterForKey(InstanceKey);
        _ = TryAcquireSingleInstanceMutex();
    }

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
