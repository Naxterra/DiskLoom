using Microsoft.Windows.ApplicationModel.Resources;

namespace DiskLoom.Services;

public static class LocalizationService
{
    private static readonly ResourceLoader ResourceLoader = new();

    public static string Get(string key)
    {
        try
        {
            var value = ResourceLoader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
        }
        catch
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), arguments);
}
