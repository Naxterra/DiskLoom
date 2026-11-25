using Microsoft.UI.Xaml;
using System.Globalization;
using System.Runtime.InteropServices;
using DiskLoom.Services;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace DiskLoom;

public partial class App : Application
{
    private readonly Queue<AppActivationArguments> _redirectedActivations = [];
    private readonly object _activationLock = new();
    private DispatcherQueue? _dispatcherQueue;
    public static Window Window { get; private set; } = null!;
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);
    public static string? StartupScanPath { get; private set; }
    public static string CurrentLanguage { get; private set; } = "en-US";
    internal static AppSettings Settings { get; set; } = new();

    public App()
    {
        Program.RedirectedActivation += Program_RedirectedActivation;
        Settings = AppSettings.Load();
        CurrentLanguage = AppSettings.ResolveLanguage(Settings);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = CurrentLanguage;
        var uiCulture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;

        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }

        UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            // A failing command (a file picker, a dialog) must not close the app and throw away the scan;
            // once the window is up, the error is shown and the app keeps running.
            if (Window is MainWindow window && window.TryReportError(args.Exception))
            {
                args.Handled = true;
            }
        };
        StartupScanPath = ParseScanPath(Environment.GetCommandLineArgs());
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Window = new MainWindow();
            _dispatcherQueue = Window.DispatcherQueue;
            Window.Activate();
            ProcessRedirectedActivations();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }
    }

    private void Program_RedirectedActivation(object? sender, AppActivationArguments args)
    {
        lock (_activationLock)
        {
            _redirectedActivations.Enqueue(args);
        }
        _dispatcherQueue?.TryEnqueue(ProcessRedirectedActivations);
    }

    private void ProcessRedirectedActivations()
    {
        while (true)
        {
            AppActivationArguments activation;
            lock (_activationLock)
            {
                if (!_redirectedActivations.TryDequeue(out activation!))
                {
                    return;
                }
            }

            if (Window is MainWindow window)
            {
                window.HandleRedirectedActivation(ParseScanPath(activation));
            }
        }
    }

    private static string? ParseScanPath(AppActivationArguments activation)
    {
        if (activation.Data is not ILaunchActivatedEventArgs launch || string.IsNullOrWhiteSpace(launch.Arguments))
        {
            return null;
        }

        return ParseScanPath(SplitCommandLine(launch.Arguments));
    }

    private static string? ParseScanPath(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--scan", StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }

    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var argumentPointer = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argumentPointer == nint.Zero)
        {
            return [];
        }

        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argumentPointer, index * nint.Size);
                arguments[index] = Marshal.PtrToStringUni(valuePointer) ?? string.Empty;
            }
            return arguments;
        }
        finally
        {
            LocalFree(argumentPointer);
        }
    }

    public static void RestartWithLanguage(string language, string? scanPath = null)
    {
        if (language is not ("en-US" or "de-DE") || language == CurrentLanguage)
        {
            return;
        }

        Settings = Settings with { Language = language };
        AppSettings.Save(Settings);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(executablePath)
        {
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(scanPath))
        {
            startInfo.ArgumentList.Add("--scan");
            startInfo.ArgumentList.Add(scanPath);
        }
        Program.ReleaseInstanceKeyForRestart();
        try
        {
            System.Diagnostics.Process.Start(startInfo);
            Window.Close();
        }
        catch
        {
            Program.ReclaimInstanceKeyAfterFailedRestart();
            throw;
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskLoom");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTimeOffset.Now:O}\r\n{exception}\r\n\r\n");
        }
        catch
        {
            // Diagnostics must never hide the original failure.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
