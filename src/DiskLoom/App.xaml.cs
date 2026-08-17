using Microsoft.UI.Xaml;
using System.Globalization;

namespace DiskLoom;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);
    public static string? StartupScanPath { get; private set; }
    public static string CurrentLanguage { get; private set; } = "en-US";

    public App()
    {
        CurrentLanguage = LoadLanguage();
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

        UnhandledException += (_, args) => WriteCrashLog(args.Exception);
        StartupScanPath = ParseScanPath(Environment.GetCommandLineArgs());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Window = new MainWindow();
            Window.Activate();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }
    }

    private static string? ParseScanPath(IReadOnlyList<string> arguments)
    {
        for (var index = 1; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--scan", StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }

    public static void RestartWithLanguage(string language, string? scanPath = null)
    {
        if (language is not ("en-US" or "de-DE") || language == CurrentLanguage)
        {
            return;
        }

        var settingsDirectory = GetSettingsDirectory();
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "language.txt"), language);

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
        System.Diagnostics.Process.Start(startInfo);
        Window.Close();
    }

    private static string LoadLanguage()
    {
        var environmentOverride = Environment.GetEnvironmentVariable("DISKLOOM_LANGUAGE");
        if (environmentOverride is "en-US" or "de-DE")
        {
            return environmentOverride;
        }

        try
        {
            var path = Path.Combine(GetSettingsDirectory(), "language.txt");
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { } saved && saved is "en-US" or "de-DE")
            {
                return saved;
            }
        }
        catch
        {
            // Fall back to the Windows UI language if settings are unavailable.
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? "de-DE"
            : "en-US";
    }

    private static string GetSettingsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskLoom");

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

}
