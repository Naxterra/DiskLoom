using System.Globalization;
using System.Text.Json;
using DiskLoom.Core.Services;

namespace DiskLoom.Services;

internal sealed record AppSettings
{
    public string? Language { get; init; }
    public bool ExcludeSystemFolders { get; init; }
    public bool DisplayAllocatedSize { get; init; }
    public double TreePaneWidth { get; init; } = 420;
    public Dictionary<string, double> ColumnWidths { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskLoom");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded is not null)
                {
                    return loaded with
                    {
                        ColumnWidths = loaded.ColumnWidths is null
                            ? []
                            : new Dictionary<string, double>(loaded.ColumnWidths, StringComparer.OrdinalIgnoreCase)
                    };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Damaged settings fall back to defaults.
        }

        return new AppSettings
        {
            Language = ReadLegacyLanguage()
        };
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings still apply for this session.
        }
    }

    public static string ResolveLanguage(AppSettings settings)
    {
        var environmentOverride = Environment.GetEnvironmentVariable("DISKLOOM_LANGUAGE");
        if (environmentOverride is "en-US" or "de-DE")
        {
            return environmentOverride;
        }

        if (settings.Language is "en-US" or "de-DE")
        {
            return settings.Language;
        }

        if (ReadLegacyLanguage() is { } legacy)
        {
            return legacy;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? "de-DE"
            : "en-US";
    }

    private static string? ReadLegacyLanguage()
    {
        try
        {
            var path = Path.Combine(SettingsDirectory, "language.txt");
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { } saved && saved is "en-US" or "de-DE")
            {
                return saved;
            }
        }
        catch
        {
            // Ignore unreadable legacy language file.
        }
        return null;
    }
}
