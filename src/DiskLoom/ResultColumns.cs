using System.Security;
using System.Text;
using System.Text.Json;
using DiskLoom.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace DiskLoom;

public enum ResultColumnKey
{
    Name,
    Size,
    Allocated,
    Percent,
    Files,
    Folders,
    Modified,
    Created,
    Accessed,
    Type,
    Attributes
}

// SortBox lists these in the same order, so the numeric values are its item indices.
internal enum ResultSortColumn
{
    Size = 0,
    Allocated = 1,
    Name = 2,
    Modified = 3,
    FileCount = 4,
    FolderCount = 5,
    Created = 6,
    Accessed = 7,
    Type = 8,
    Attributes = 9
}

internal sealed record ResultColumnDefinition(
    ResultColumnKey Key,
    string TitleResource,
    double MinWidth,
    bool AlignRight,
    ResultSortColumn Sort,
    Func<NodeRow, string> Text)
{
    public string Title => LocalizationService.Get(TitleResource);
    public string WidthProperty => $"{Key}Width";
}

internal static class ResultColumns
{
    public static IReadOnlyList<ResultColumnDefinition> All { get; } = new ResultColumnDefinition[]
    {
        new(ResultColumnKey.Name, "SortNameLabel", 140, false, ResultSortColumn.Name, static row => row.Name),
        new(ResultColumnKey.Size, "SortSizeLabel", 96, true, ResultSortColumn.Size, static row => row.SizeText),
        new(ResultColumnKey.Allocated, "SortAllocatedLabel", 110, true, ResultSortColumn.Allocated, static row => row.AllocatedText),
        // Share of the parent folder is size-ordered, so it sorts by size.
        new(ResultColumnKey.Percent, "ColumnPercent", 120, false, ResultSortColumn.Size, static row => row.PercentText),
        new(ResultColumnKey.Files, "ColumnFiles", 80, true, ResultSortColumn.FileCount, static row => row.FileCountText),
        new(ResultColumnKey.Folders, "ColumnFolders", 80, true, ResultSortColumn.FolderCount, static row => row.FolderCountText),
        new(ResultColumnKey.Modified, "SortModifiedLabel", 135, true, ResultSortColumn.Modified, static row => row.ModifiedText),
        new(ResultColumnKey.Created, "ColumnCreated", 135, true, ResultSortColumn.Created, static row => row.CreatedText),
        new(ResultColumnKey.Accessed, "ColumnAccessed", 135, true, ResultSortColumn.Accessed, static row => row.AccessedText),
        new(ResultColumnKey.Type, "ColumnType", 80, false, ResultSortColumn.Type, static row => row.Kind),
        new(ResultColumnKey.Attributes, "ColumnAttributes", 70, false, ResultSortColumn.Attributes, static row => row.AttributesText)
    };

    public static IReadOnlyList<ResultColumnKey> Default { get; } = new[]
    {
        ResultColumnKey.Name,
        ResultColumnKey.Size,
        ResultColumnKey.Percent,
        ResultColumnKey.Allocated,
        ResultColumnKey.Modified
    };

    public static ResultColumnDefinition Get(ResultColumnKey key) => All[(int)key];

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskLoom",
        "columns.json");

    // Visible columns in display order; Name is always present and first.
    public static IReadOnlyList<ResultColumnKey> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(SettingsPath)) ?? [];
                return Normalize(names
                    .Select(static name => Enum.TryParse<ResultColumnKey>(name, out var key) ? key : (ResultColumnKey?)null)
                    .OfType<ResultColumnKey>());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable settings file falls back to the default columns.
        }
        return Default;
    }

    public static void Save(IReadOnlyList<ResultColumnKey> columns)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(columns.Select(static key => key.ToString()).ToArray()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Column choices still apply for this session.
        }
    }

    public static IReadOnlyList<ResultColumnKey> Normalize(IEnumerable<ResultColumnKey> columns) =>
        new[] { ResultColumnKey.Name }
            .Concat(columns.Where(static key => key != ResultColumnKey.Name && Enum.IsDefined(key)))
            .Distinct()
            .ToArray();

    // Grid columns: 0 = icon, then each result column followed by a 6 px divider/splitter column.
    public static int GridColumn(int position) => 1 + position * 2;

    public static DataTemplate CreateRowTemplate(IReadOnlyList<ResultColumnDefinition> columns)
    {
        var xaml = new StringBuilder();
        xaml.Append($$"""<DataTemplate {{XamlNamespaces}}><Grid Padding="6,3"><Grid.ColumnDefinitions><ColumnDefinition Width="28" />""");
        foreach (var column in columns)
        {
            xaml.Append($$"""<ColumnDefinition Width="{Binding ColumnLayout.{{column.WidthProperty}}}" MinWidth="{{column.MinWidth}}" /><ColumnDefinition Width="6" />""");
        }
        xaml.Append("""</Grid.ColumnDefinitions><FontIcon Glyph="{Binding Glyph}" FontSize="15" Foreground="{ThemeResource DiskLoomBlueBrush}" VerticalAlignment="Center" />""");

        for (var position = 0; position < columns.Count; position++)
        {
            var column = columns[position];
            var gridColumn = GridColumn(position);
            xaml.Append(column.Key switch
            {
                ResultColumnKey.Name => $$"""<StackPanel Grid.Column="{{gridColumn}}" Margin="0,0,6,0" VerticalAlignment="Center"><TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" /><TextBlock Text="{Binding CountText}" FontSize="10" Style="{StaticResource MutedTextStyle}" /></StackPanel>""",
                ResultColumnKey.Percent => $$"""<Grid Grid.Column="{{gridColumn}}" Margin="6,0" ColumnSpacing="6" VerticalAlignment="Center"><Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions><ProgressBar Value="{Binding PercentOfParent}" Maximum="100" MinWidth="24" VerticalAlignment="Center" /><TextBlock Grid.Column="1" MinWidth="46" Text="{Binding PercentText}" TextAlignment="Right" VerticalAlignment="Center" /></Grid>""",
                _ => $$"""<TextBlock Grid.Column="{{gridColumn}}" Margin="6,0" Text="{Binding {{BindingPath(column.Key)}}}" HorizontalAlignment="{{(column.AlignRight ? "Right" : "Left")}}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />"""
            });
            xaml.Append($$"""<Border Grid.Column="{{gridColumn + 1}}" Width="1" HorizontalAlignment="Center" Background="{ThemeResource DividerStrokeColorDefaultBrush}" />""");
        }

        xaml.Append("</Grid></DataTemplate>");
        return (DataTemplate)XamlReader.Load(xaml.ToString());
    }

    // The header binds its widths to the same ResultColumnLayout (its DataContext) as the rows.
    public static Grid CreateHeader(IReadOnlyList<ResultColumnDefinition> columns)
    {
        var xaml = new StringBuilder();
        xaml.Append($$"""<Grid {{XamlNamespaces}} Padding="6,2" Background="{ThemeResource DiskLoomResultHeaderBrush}"><Grid.ColumnDefinitions><ColumnDefinition Width="28" />""");
        foreach (var column in columns)
        {
            xaml.Append($$"""<ColumnDefinition Width="{Binding {{column.WidthProperty}}}" MinWidth="{{column.MinWidth}}" /><ColumnDefinition Width="6" />""");
        }
        xaml.Append("</Grid.ColumnDefinitions>");

        for (var position = 0; position < columns.Count; position++)
        {
            var column = columns[position];
            var gridColumn = GridColumn(position);
            var margin = column.Key == ResultColumnKey.Name ? "0,0,6,0" : "6,0";
            xaml.Append($$"""<Button Grid.Column="{{gridColumn}}" Tag="{{column.Key}}" Margin="{{margin}}" Style="{StaticResource ColumnHeaderButtonStyle}" HorizontalContentAlignment="{{(column.AlignRight ? "Right" : "Left")}}"><TextBlock Text="{{SecurityElement.Escape(column.Title)}}" TextTrimming="CharacterEllipsis" /></Button>""");
            xaml.Append($$"""<Thumb Grid.Column="{{gridColumn + 1}}" Tag="{{column.Key}}" Style="{StaticResource ResultColumnSplitterStyle}" />""");
        }

        xaml.Append("</Grid>");
        return (Grid)XamlReader.Load(xaml.ToString());
    }

    private static string BindingPath(ResultColumnKey key) => key switch
    {
        ResultColumnKey.Size => nameof(NodeRow.SizeText),
        ResultColumnKey.Allocated => nameof(NodeRow.AllocatedText),
        ResultColumnKey.Files => nameof(NodeRow.FileCountText),
        ResultColumnKey.Folders => nameof(NodeRow.FolderCountText),
        ResultColumnKey.Modified => nameof(NodeRow.ModifiedText),
        ResultColumnKey.Created => nameof(NodeRow.CreatedText),
        ResultColumnKey.Accessed => nameof(NodeRow.AccessedText),
        ResultColumnKey.Type => nameof(NodeRow.Kind),
        ResultColumnKey.Attributes => nameof(NodeRow.AttributesText),
        _ => nameof(NodeRow.Name)
    };

    private const string XamlNamespaces =
        "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
}
