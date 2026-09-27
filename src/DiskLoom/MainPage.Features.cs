using System.Diagnostics;
using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using DiskLoom.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace DiskLoom;

// Live results, configurable columns, tree filter, search, charts, copy/move and report export.
public sealed partial class MainPage
{
    private const int ChartSliceLimit = 12;
    private const int ReportRowLimit = 25_000;

    private readonly Dictionary<string, NodeRow> _liveRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ResultColumnKey, TextBlock> _resultHeaderTexts = [];
    private readonly Dictionary<ResultColumnKey, ColumnDefinition> _resultHeaderColumns = [];
    private IReadOnlyList<ScanNode>? _liveTopLevel;
    private IReadOnlyList<ResultColumnKey> _visibleColumns = ResultColumns.Default;
    private ScanPresentation? _basePresentation;
    private TreeFilter _treeFilter = new();
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<ChartSliceRow> _chartRows = [];
    private long _chartRequestId;
    private ExportFormat _exportFormat = ExportFormat.Excel;
    private int _exportDepth = 3;
    private bool _exportAllLevels;
    private bool _exportIncludeFiles = true;
    private bool _exportCurrentFolder;

    private enum ExportFormat
    {
        Csv,
        Json,
        Excel,
        Html,
        Pdf
    }

    private void InitializeFeatureControls()
    {
        var typeChoices = new[] { LocalizationService.Get("CategoryAny") }
            .Concat(FileTypeCategories.All.Select(static category => LocalizationService.Get($"Category{category}")))
            .ToArray();
        TreeFilterTypeBox.ItemsSource = typeChoices;
        TreeFilterTypeBox.SelectedIndex = 0;
        SearchTypeBox.ItemsSource = typeChoices;
        SearchTypeBox.SelectedIndex = 0;
        TreeFilterAgeBox.ItemsSource = AgeChoices.Select(static choice => LocalizationService.Get(choice.Resource)).ToArray();
        TreeFilterAgeBox.SelectedIndex = 0;
    }

    private static FileTypeCategory CategoryFromIndex(int index) =>
        index <= 0 || index > FileTypeCategories.All.Count ? FileTypeCategory.Any : FileTypeCategories.All[index - 1];

    // Filter age choices: (resource, older than days, newer than days).
    private static readonly (string Resource, int? OlderThan, int? NewerThan)[] AgeChoices =
    [
        ("AgeAny", null, null),
        ("AgeWithin7", null, 7),
        ("AgeWithin30", null, 30),
        ("AgeWithinYear", null, 365),
        ("AgeOlder30", 30, null),
        ("AgeOlder180", 180, null),
        ("AgeOlderYearChoice", 365, null),
        ("AgeOlder3Years", 3 * 365, null)
    ];

    // ---- Configurable result columns ---------------------------------------------------------

    private void ApplyResultColumns(IReadOnlyList<ResultColumnKey> keys)
    {
        _visibleColumns = ResultColumns.Normalize(keys);
        var columns = _visibleColumns.Select(ResultColumns.Get).ToArray();

        var header = ResultColumns.CreateHeader(columns);
        header.DataContext = ResultColumnWidths;
        header.ContextFlyout = CreateColumnsMenu();
        _resultHeaderTexts.Clear();
        _resultHeaderColumns.Clear();
        var resizeTip = LocalizationService.Get("ResultColumnResizeTip");
        foreach (var element in header.Children.OfType<FrameworkElement>())
        {
            if (element.Tag is not string tag || !Enum.TryParse<ResultColumnKey>(tag, out var key))
            {
                continue;
            }

            switch (element)
            {
                case Button button:
                    button.Click += SortHeader_Click;
                    if (button.Content is TextBlock text)
                    {
                        _resultHeaderTexts[key] = text;
                    }
                    _resultHeaderColumns[key] = header.ColumnDefinitions[Grid.GetColumn(button)];
                    break;
                case Thumb splitter:
                    splitter.DragDelta += ResultColumnSplitter_DragDelta;
                    splitter.DoubleTapped += ResultColumnSplitter_DoubleTapped;
                    ToolTipService.SetToolTip(splitter, resizeTip);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(splitter, resizeTip);
                    break;
            }
        }

        ResultHeaderHost.Child = header;
        ChildrenList.ItemTemplate = ResultColumns.CreateRowTemplate(columns);
        UpdateSortIndicators();
    }

    private MenuFlyout CreateColumnsMenu()
    {
        var menu = new MenuFlyout();
        foreach (var column in ResultColumns.All.Where(static column => column.Key != ResultColumnKey.Name))
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = column.Title,
                IsChecked = _visibleColumns.Contains(column.Key),
                Tag = column.Key
            };
            item.Click += (_, _) =>
            {
                var updated = item.IsChecked
                    ? _visibleColumns.Append(column.Key).ToArray()
                    : _visibleColumns.Where(key => key != column.Key).ToArray();
                SetResultColumns(updated);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var customize = new MenuFlyoutItem { Text = LocalizationService.Get("CustomizeColumnsLabel") };
        customize.Click += CustomizeColumns_Click;
        menu.Items.Add(customize);
        var reset = new MenuFlyoutItem { Text = LocalizationService.Get("ResetColumnsMenu") };
        reset.Click += (_, _) => SetResultColumns(ResultColumns.Default);
        menu.Items.Add(reset);
        return menu;
    }

    private void SetResultColumns(IReadOnlyList<ResultColumnKey> keys)
    {
        ApplyResultColumns(keys);
        ResultColumns.Save(_visibleColumns);
    }

    private async void CustomizeColumns_Click(object sender, RoutedEventArgs e)
    {
        // Visible columns first in their current order, then the hidden ones.
        var choices = new System.Collections.ObjectModel.ObservableCollection<ColumnChoice>(
            _visibleColumns
                .Concat(ResultColumns.All.Select(static column => column.Key).Where(key => !_visibleColumns.Contains(key)))
                .Select(key => new ColumnChoice(key, ResultColumns.Get(key).Title, _visibleColumns.Contains(key))));
        var list = new ListView
        {
            ItemsSource = choices,
            SelectionMode = ListViewSelectionMode.Single,
            CanReorderItems = true,
            CanDragItems = true,
            AllowDrop = true,
            Height = 360,
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <CheckBox Content="{Binding Title}" IsChecked="{Binding IsVisible, Mode=TwoWay}" IsEnabled="{Binding CanHide}" />
                </DataTemplate>
                """)
        };
        var up = new Button { Content = LocalizationService.Get("MoveUp") };
        var down = new Button { Content = LocalizationService.Get("MoveDown") };
        void Move(int offset)
        {
            if (list.SelectedItem is not ColumnChoice selected)
            {
                return;
            }
            var index = choices.IndexOf(selected);
            var target = index + offset;
            // Name stays first.
            if (selected.Key == ResultColumnKey.Name || target < 1 || target >= choices.Count)
            {
                return;
            }
            choices.Move(index, target);
            list.SelectedItem = selected;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("ColumnsDialogTitle"),
            PrimaryButtonText = LocalizationService.Get("Apply"),
            SecondaryButtonText = LocalizationService.Get("ResetColumnsMenu"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 320,
                Children =
                {
                    new TextBlock { Text = LocalizationService.Get("ColumnsDialogHint"), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["MutedTextStyle"] },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { up, down } }
                }
            }
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                SetResultColumns(choices.Where(static choice => choice.IsVisible).Select(static choice => choice.Key).ToArray());
                break;
            case ContentDialogResult.Secondary:
                SetResultColumns(ResultColumns.Default);
                break;
        }
    }

    // ---- Live results while scanning --------------------------------------------------------

    private void ShowLiveResults(IReadOnlyList<ScanNode> topLevel)
    {
        _liveTopLevel = topLevel;
        // Shares are of everything found so far, so they add up to 100 % at every moment.
        var total = topLevel.Sum(static node => node.Size);
        const bool displayAllocated = DisplayAllocatedMeasurements;
        var rows = SortNodes(FilterByQuery(topLevel))
            .Take(5_000)
            .Select(node =>
            {
                if (_liveRows.TryGetValue(node.FullPath, out var row))
                {
                    row.Update(node, total);
                    return row;
                }
                row = new NodeRow(node, displayAllocated, ResultColumnWidths, total, isLive: true);
                _liveRows[node.FullPath] = row;
                return row;
            })
            .ToArray();

        // Replacing the item source only when the order changed keeps the selection and scroll position.
        if (ChildrenList.ItemsSource is not NodeRow[] current || !current.SequenceEqual(rows))
        {
            ChildrenList.ItemsSource = rows;
        }
        AllocatedSizeText.Text = ByteFormatter.Format(topLevel.Sum(static node => node.AllocatedSize));
        TreeCountText.Text = LocalizationService.Format("LiveCount", topLevel.Count);
        if (ReferenceEquals(WorkspaceTabs.SelectedItem, TreemapTab))
        {
            RenderTreemap();
        }
    }

    private void ClearLiveResults()
    {
        var hadLiveResults = _liveTopLevel is not null;
        _liveTopLevel = null;
        _liveRows.Clear();
        if (hadLiveResults && _currentNode is null)
        {
            ChildrenList.ItemsSource = null;
            TreemapCanvas.Children.Clear();
        }
    }

    // ---- Tree filter -----------------------------------------------------------------------

    private TreeFilter ReadTreeFilter()
    {
        var age = AgeChoices[Math.Clamp(TreeFilterAgeBox.SelectedIndex, 0, AgeChoices.Length - 1)];
        return new TreeFilter
        {
            Category = CategoryFromIndex(TreeFilterTypeBox.SelectedIndex),
            NamePattern = TreeFilterPatternBox.Text.Trim(),
            OlderThanDays = age.OlderThan,
            NewerThanDays = age.NewerThan
        };
    }

    private async void TreeFilterApply_Click(object sender, RoutedEventArgs e)
    {
        TreeFilterFlyout.Hide();
        await ApplyTreeFilterAsync(ReadTreeFilter());
    }

    private async void TreeFilterPatternBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            TreeFilterFlyout.Hide();
            await ApplyTreeFilterAsync(ReadTreeFilter());
        }
    }

    private async void TreeFilterClear_Click(object sender, RoutedEventArgs e)
    {
        TreeFilterFlyout.Hide();
        TreeFilterTypeBox.SelectedIndex = 0;
        TreeFilterAgeBox.SelectedIndex = 0;
        TreeFilterPatternBox.Text = string.Empty;
        await ApplyTreeFilterAsync(new TreeFilter());
    }

    private async Task ApplyTreeFilterAsync(TreeFilter filter)
    {
        if (_basePresentation is not { } basis || _workCancellation is not null)
        {
            return;
        }

        var preferredPath = _currentNode?.FullPath;
        if (filter.IsEmpty)
        {
            if (!_treeFilter.IsEmpty)
            {
                _treeFilter = filter;
                DisplayPresentation(basis, preferredPath);
                UpdateFilterBanner();
                StatusText.Text = LocalizationService.Get("FilterCleared");
            }
            return;
        }

        SetBusy(true, LocalizationService.Get("ApplyingFilter"));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var presentation = await Task.Run(() => CreatePresentation(ScanTreeFilter.Apply(basis.Result, filter, DateTimeOffset.UtcNow)));
            if (!ReferenceEquals(_basePresentation, basis))
            {
                return;
            }

            _treeFilter = filter;
            DisplayPresentation(presentation, preferredPath);
            UpdateFilterBanner();
            var root = presentation.Result.Root;
            StatusText.Text = LocalizationService.Format("FilterApplied", root.FileCount, ByteFormatter.Format(root.Size));
            StatusDetailText.Text = LocalizationService.Format("ElapsedMilliseconds", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ResetTreeFilterState()
    {
        _treeFilter = new TreeFilter();
        UpdateFilterBanner();
    }

    private void UpdateFilterBanner()
    {
        if (_treeFilter.IsEmpty)
        {
            FilterBanner.Visibility = Visibility.Collapsed;
            FilterButton.Label = LocalizationService.Get("FilterButtonLabel");
            return;
        }

        var parts = new List<string>();
        if (_treeFilter.Category != FileTypeCategory.Any)
        {
            parts.Add(LocalizationService.Get($"Category{_treeFilter.Category}"));
        }
        if (!string.IsNullOrWhiteSpace(_treeFilter.NamePattern))
        {
            parts.Add($"\"{_treeFilter.NamePattern}\"");
        }
        var age = AgeChoices.FirstOrDefault(choice => choice.OlderThan == _treeFilter.OlderThanDays && choice.NewerThan == _treeFilter.NewerThanDays);
        if (age.Resource is not null and not "AgeAny")
        {
            parts.Add(LocalizationService.Get(age.Resource));
        }
        FilterBannerText.Text = LocalizationService.Format("FilterBanner", string.Join(" · ", parts));
        FilterBanner.Visibility = Visibility.Visible;
        FilterButton.Label = LocalizationService.Get("FilterActiveLabel");
    }

    // ---- Search ----------------------------------------------------------------------------

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void SearchNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        _searchCancellation?.Cancel();
        SearchNameBox.Text = string.Empty;
        SearchTypeBox.SelectedIndex = 0;
        SearchKindBox.SelectedIndex = 0;
        SearchMinSizeBox.Value = double.NaN;
        SearchMaxSizeBox.Value = double.NaN;
        SearchAfterPicker.Date = null;
        SearchBeforePicker.Date = null;
        SearchCurrentFolderBox.IsChecked = false;
        SearchResultsList.ItemsSource = null;
        SearchSummaryText.Text = LocalizationService.Get("SearchSummaryDefault");
    }

    private async Task RunSearchAsync()
    {
        if (_scanResult is not { } result)
        {
            ShowNotification(LocalizationService.Get("ScanBeforeSearch"), InfoBarSeverity.Warning);
            return;
        }

        static long? Megabytes(double value) => double.IsNaN(value) ? null : (long)(Math.Max(0, value) * 1024 * 1024);
        var criteria = new SearchCriteria
        {
            NamePattern = SearchNameBox.Text.Trim(),
            Category = CategoryFromIndex(SearchTypeBox.SelectedIndex),
            Kind = SearchKindBox.SelectedIndex switch
            {
                1 => SearchItemKind.Folders,
                2 => SearchItemKind.FilesAndFolders,
                _ => SearchItemKind.Files
            },
            MinimumSize = Megabytes(SearchMinSizeBox.Value),
            MaximumSize = Megabytes(SearchMaxSizeBox.Value),
            // Picked dates are whole local days: after = from its start, before = through its end.
            ModifiedAfter = SearchAfterPicker.Date is { } after ? new DateTimeOffset(after.Date, after.Offset) : null,
            ModifiedBefore = SearchBeforePicker.Date is { } before ? new DateTimeOffset(before.Date.AddDays(1).AddTicks(-1), before.Offset) : null
        };
        var root = SearchCurrentFolderBox.IsChecked == true && _currentNode is { } current ? current : result.Root;

        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SearchSummaryText.Text = LocalizationService.Get("Searching");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var found = await Task.Run(() => ScanSearch.Search(root, criteria, cancellation.Token), cancellation.Token);
            if (!ReferenceEquals(_searchCancellation, cancellation) || !ReferenceEquals(_scanResult, result))
            {
                return;
            }

            const bool displayAllocated = DisplayAllocatedMeasurements;
            SearchResultsList.ItemsSource = found.Matches
                .Where(node => !IsDeletedPath(node.FullPath))
                .Select(node => new NodeRow(node, displayAllocated))
                .ToArray();
            SearchSummaryText.Text = LocalizationService.Format(
                found.IsTruncated ? "SearchSummaryTruncated" : "SearchSummary",
                found.MatchCount,
                ByteFormatter.Format(found.MatchedSize),
                found.Matches.Count,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // A newer search or a reset replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
            }
        }
    }

    private void SearchResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not NodeRow row)
        {
            return;
        }

        if (row.Source.IsDirectory && _nodesByPath.TryGetValue(row.Path, out var folder))
        {
            ShowNode(folder);
            WorkspaceTabs.SelectedItem = OverviewTab;
        }
        else
        {
            _fileOperations.ShowInExplorer(row.Path);
        }
    }

    // ---- Charts ----------------------------------------------------------------------------

    private static readonly Color[] ChartPalette =
    [
        Color.FromArgb(255, 18, 104, 196),
        Color.FromArgb(255, 0, 162, 199),
        Color.FromArgb(255, 45, 184, 140),
        Color.FromArgb(255, 108, 61, 193),
        Color.FromArgb(255, 230, 145, 30),
        Color.FromArgb(255, 204, 72, 110),
        Color.FromArgb(255, 84, 168, 255),
        Color.FromArgb(255, 122, 182, 36),
        Color.FromArgb(255, 179, 140, 255),
        Color.FromArgb(255, 214, 102, 44),
        Color.FromArgb(255, 0, 123, 98),
        Color.FromArgb(255, 150, 110, 70)
    ];

    private static readonly Color OtherSliceColor = Color.FromArgb(255, 128, 138, 150);

    private void ChartOptions_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshChartIfVisible();

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private async void RefreshChartIfVisible()
    {
        if (!_loaded || !ReferenceEquals(WorkspaceTabs.SelectedItem, ChartsTab))
        {
            return;
        }
        if (_currentNode is not { } folder)
        {
            ClearChart();
            return;
        }

        var kind = (BreakdownKind)Math.Max(0, ChartKindBox.SelectedIndex);
        var requestId = Interlocked.Increment(ref _chartRequestId);
        IReadOnlyList<BreakdownSlice> slices;
        try
        {
            // Subfolders are already summed; the other breakdowns walk every file below the folder.
            slices = kind == BreakdownKind.Children
                ? FolderBreakdown.Create(folder, kind, ChartSliceLimit, DateTimeOffset.UtcNow)
                : await Task.Run(() => FolderBreakdown.Create(folder, kind, ChartSliceLimit, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
            return;
        }
        if (requestId != Volatile.Read(ref _chartRequestId) || !ReferenceEquals(_currentNode, folder))
        {
            return;
        }

        var visible = slices.Where(slice => slice.Node is null || !IsDeletedPath(slice.Node.FullPath)).ToArray();
        var total = Math.Max(1, visible.Sum(static slice => slice.Size));
        var colorIndex = 0;
        _chartRows = visible
            .Select(slice => new ChartSliceRow(
                ChartLabel(slice, kind),
                slice.Size,
                slice.FileCount,
                slice.Size * 100d / total,
                new SolidColorBrush(slice.IsOther ? OtherSliceColor : ChartPalette[colorIndex++ % ChartPalette.Length]),
                slice.Node))
            .ToArray();
        ChartLegendList.ItemsSource = _chartRows;
        ChartTitleText.Text = LocalizationService.Format("ChartTitle", folder.FullPath, ByteFormatter.Format(visible.Sum(static slice => slice.Size)));
        DrawChart();
    }

    private static string ChartLabel(BreakdownSlice slice, BreakdownKind kind) => slice.IsOther
        ? LocalizationService.Get("ChartOther")
        : kind switch
        {
            BreakdownKind.Extensions => slice.Label == "(no extension)" ? LocalizationService.Get("NoExtension") : slice.Label,
            BreakdownKind.Categories => LocalizationService.Get($"Category{slice.Label}"),
            BreakdownKind.Ages => AgeRow.LocalizeAge(slice.Label),
            _ => slice.Label
        };

    private void ClearChart()
    {
        Interlocked.Increment(ref _chartRequestId);
        _chartRows = [];
        ChartLegendList.ItemsSource = null;
        ChartTitleText.Text = string.Empty;
        ChartCanvas.Children.Clear();
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        var rows = _chartRows.Where(static row => row.Size > 0).ToArray();
        if (rows.Length == 0 || width < 40 || height < 40)
        {
            return;
        }

        if (ChartStyleBox.SelectedIndex == 1)
        {
            DrawBars(rows, width, height);
        }
        else
        {
            DrawPie(rows, width, height);
        }
    }

    private void DrawPie(IReadOnlyList<ChartSliceRow> rows, double width, double height)
    {
        var outer = Math.Min(width, height) / 2 - 8;
        var inner = outer * 0.56;
        var center = new Point(width / 2, height / 2);
        var total = rows.Sum(static row => (double)row.Size);
        var angle = -90d;
        foreach (var row in rows)
        {
            var sweep = row.Size / total * 360;
            FrameworkElement shape;
            if (sweep >= 359.99)
            {
                // A single arc cannot close a full ring; draw it as a thick circle instead.
                shape = new Ellipse
                {
                    Width = outer + inner,
                    Height = outer + inner,
                    Stroke = row.Brush,
                    StrokeThickness = outer - inner
                };
                Canvas.SetLeft(shape, center.X - (outer + inner) / 2);
                Canvas.SetTop(shape, center.Y - (outer + inner) / 2);
            }
            else
            {
                shape = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Fill = row.Brush,
                    Stroke = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    StrokeThickness = 1.5,
                    Data = CreateRingSegment(center, outer, inner, angle, sweep)
                };
            }
            AttachSliceInteraction(shape, row);
            ChartCanvas.Children.Add(shape);
            angle += sweep;
        }

        var totalText = new TextBlock
        {
            Text = ByteFormatter.Format(rows.Sum(static row => row.Size)),
            FontSize = Math.Clamp(inner / 4, 12, 26),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = inner * 1.6,
            IsHitTestVisible = false
        };
        totalText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(totalText, center.X - totalText.Width / 2);
        Canvas.SetTop(totalText, center.Y - totalText.DesiredSize.Height / 2);
        ChartCanvas.Children.Add(totalText);
    }

    private static PathGeometry CreateRingSegment(Point center, double outer, double inner, double startAngle, double sweep)
    {
        Point At(double radius, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        var largeArc = sweep > 180;
        var endAngle = startAngle + sweep;
        var figure = new PathFigure { StartPoint = At(outer, startAngle), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment { Point = At(outer, endAngle), Size = new Size(outer, outer), IsLargeArc = largeArc, SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = At(inner, endAngle) });
        figure.Segments.Add(new ArcSegment { Point = At(inner, startAngle), Size = new Size(inner, inner), IsLargeArc = largeArc, SweepDirection = SweepDirection.Counterclockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void DrawBars(IReadOnlyList<ChartSliceRow> rows, double width, double height)
    {
        const double labelWidthShare = 0.32;
        var rowHeight = Math.Clamp(height / rows.Count, 22, 44);
        var barHeight = rowHeight * 0.62;
        var labelWidth = Math.Min(260, width * labelWidthShare);
        var valueWidth = 150d;
        var barArea = Math.Max(20, width - labelWidth - valueWidth - 16);
        var largest = Math.Max(1, rows.Max(static row => row.Size));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var top = index * rowHeight;
            var label = new TextBlock { Text = row.Label, Width = labelWidth - 8, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, top + (rowHeight - 20) / 2);
            ChartCanvas.Children.Add(label);

            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(2, row.Size / (double)largest * barArea),
                Height = barHeight,
                RadiusX = 3,
                RadiusY = 3,
                Fill = row.Brush
            };
            Canvas.SetLeft(bar, labelWidth);
            Canvas.SetTop(bar, top + (rowHeight - barHeight) / 2);
            AttachSliceInteraction(bar, row);
            ChartCanvas.Children.Add(bar);

            var value = new TextBlock { Text = $"{row.SizeText} · {row.PercentText}", Style = (Style)Application.Current.Resources["MutedTextStyle"] };
            Canvas.SetLeft(value, labelWidth + bar.Width + 8);
            Canvas.SetTop(value, top + (rowHeight - 20) / 2);
            ChartCanvas.Children.Add(value);
        }
    }

    private void AttachSliceInteraction(FrameworkElement shape, ChartSliceRow row)
    {
        ToolTipService.SetToolTip(shape, row.ToolTip);
        if (row.Node is { IsDirectory: true } folder)
        {
            shape.DoubleTapped += (_, _) => ShowNode(folder);
        }
    }

    private void ChartLegendList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChartSliceRow { Node: { IsDirectory: true } folder })
        {
            ShowNode(folder);
        }
    }

    // ---- Copy / move -----------------------------------------------------------------------

    private async void CopyTo_Click(object sender, RoutedEventArgs e) => await TransferMenuTargetsAsync(move: false);

    private async void MoveTo_Click(object sender, RoutedEventArgs e) => await TransferMenuTargetsAsync(move: true);

    private async Task TransferMenuTargetsAsync(bool move)
    {
        var rows = GetMenuTargets();
        if (rows.Count == 0 || !rows.All(CanDeleteTarget))
        {
            return;
        }

        if (await TransferAsync(rows.Select(static row => row.Path).ToArray(), move) is { } moved && moved.Count > 0)
        {
            foreach (var path in moved)
            {
                _deletedPaths.Add(path);
            }
            _scanCache.Clear();
            RefreshListsAfterDeletion();
        }
    }

    private async void TreeCopyTo_Click(object sender, RoutedEventArgs e)
    {
        if (GetTreeContextRow() is { Source: not null } row)
        {
            await TransferAsync(new[] { row.FullPath }, move: false);
        }
    }

    private async void TreeMoveTo_Click(object sender, RoutedEventArgs e)
    {
        var row = GetTreeContextRow();
        if (row is null || !CanDeleteTreeFolder(row))
        {
            return;
        }

        if (await TransferAsync(new[] { row.FullPath }, move: true) is { Count: > 0 })
        {
            // Like deleting a folder from the tree: rescan so totals and the tree are correct.
            _scanCache.Clear();
            await RefreshAfterTreeDeletionAsync();
        }
    }

    // Returns the sources that no longer exist afterwards (moved away), or null if nothing ran.
    private async Task<IReadOnlyList<string>?> TransferAsync(IReadOnlyList<string> sources, bool move)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return null;
        }

        var destination = folder.Path;
        if (sources.Any(source => IsSameOrInside(destination, source)))
        {
            ShowNotification(LocalizationService.Get("TransferIntoItself"), InfoBarSeverity.Warning);
            return null;
        }
        if (move && sources.All(source => PathsEqual(System.IO.Path.GetDirectoryName(source), destination)))
        {
            ShowNotification(LocalizationService.Get("TransferSameFolder"), InfoBarSeverity.Informational);
            return null;
        }

        SetBusy(true, LocalizationService.Format(move ? "MovingItems" : "CopyingItems", sources.Count, destination));
        try
        {
            var result = move
                ? await _fileOperations.MoveAsync(sources, destination, App.WindowHandle)
                : await _fileOperations.CopyAsync(sources, destination, App.WindowHandle);
            var gone = move
                ? sources.Where(static source => !File.Exists(source) && !Directory.Exists(source)).ToArray()
                : [];

            var scanRoot = (_basePresentation?.Result ?? _scanResult)?.Root.FullPath;
            var landsInScan = scanRoot is not null && IsSameOrInside(destination, scanRoot);
            if (result.Succeeded)
            {
                var message = LocalizationService.Format(move ? "MovedItems" : "CopiedItems", sources.Count, destination);
                ShowNotification(landsInScan ? $"{message} {LocalizationService.Get("RescanToSeeTransfer")}" : message, InfoBarSeverity.Success);
            }
            else if (result.Aborted)
            {
                ShowNotification(LocalizationService.Get("TransferCanceled"), InfoBarSeverity.Warning);
            }
            else
            {
                ShowNotification(LocalizationService.Format("TransferFailed", result.ErrorCode), InfoBarSeverity.Error);
            }
            StatusText.Text = LocalizationService.Get("TransferFinished");
            return gone;
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var fullPath = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        var fullFolder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/');
        return fullPath.Equals(fullFolder, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullFolder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Export ----------------------------------------------------------------------------

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is not { } result)
        {
            return;
        }

        var formatBox = new RadioButtons
        {
            Header = LocalizationService.Get("ExportFormatHeader"),
            MaxColumns = 1,
            ItemsSource = new[] { "ExportFormatCsv", "ExportFormatJson", "ExportFormatExcel", "ExportFormatHtml", "ExportFormatPdf" }
                .Select(LocalizationService.Get)
                .ToArray(),
            SelectedIndex = (int)_exportFormat
        };
        var scopeBox = new ComboBox
        {
            Header = LocalizationService.Get("ExportScopeHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                LocalizationService.Format("ExportScopeScan", result.Root.FullPath),
                LocalizationService.Format("ExportScopeFolder", _currentNode?.FullPath ?? result.Root.FullPath)
            },
            SelectedIndex = _exportCurrentFolder && _currentNode is not null ? 1 : 0,
            IsEnabled = _currentNode is not null && !ReferenceEquals(_currentNode, result.Root)
        };
        var depthBox = new NumberBox
        {
            Header = LocalizationService.Get("ExportDepthHeader"),
            Minimum = 1,
            Maximum = 64,
            Value = _exportDepth,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var allLevelsBox = new CheckBox { Content = LocalizationService.Get("ExportAllLevels"), IsChecked = _exportAllLevels };
        var includeFilesBox = new CheckBox { Content = LocalizationService.Get("ExportIncludeFiles"), IsChecked = _exportIncludeFiles };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["MutedTextStyle"] };
        void UpdateControls()
        {
            var format = (ExportFormat)Math.Max(0, formatBox.SelectedIndex);
            var isJson = format == ExportFormat.Json;
            depthBox.IsEnabled = !isJson && allLevelsBox.IsChecked != true;
            allLevelsBox.IsEnabled = !isJson;
            includeFilesBox.IsEnabled = !isJson;
            hint.Text = format switch
            {
                ExportFormat.Json => LocalizationService.Get("ExportHintJson"),
                ExportFormat.Html or ExportFormat.Pdf => LocalizationService.Format("ExportHintReport", ReportRowLimit),
                ExportFormat.Excel => LocalizationService.Get("ExportHintExcel"),
                _ => LocalizationService.Get("ExportHintCsv")
            };
        }
        formatBox.SelectionChanged += (_, _) => UpdateControls();
        allLevelsBox.Click += (_, _) => UpdateControls();
        UpdateControls();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("ExportDialogTitle"),
            PrimaryButtonText = LocalizationService.Get("ExportContinue"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 380,
                Children = { formatBox, scopeBox, depthBox, allLevelsBox, includeFilesBox, hint }
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _exportFormat = (ExportFormat)Math.Max(0, formatBox.SelectedIndex);
        _exportDepth = double.IsNaN(depthBox.Value) ? 3 : (int)Math.Clamp(depthBox.Value, 1, 64);
        _exportAllLevels = allLevelsBox.IsChecked == true;
        _exportIncludeFiles = includeFilesBox.IsChecked == true;
        _exportCurrentFolder = scopeBox.SelectedIndex == 1;
        var root = _exportCurrentFolder && _currentNode is not null ? _currentNode : result.Root;
        await ExportAsync(result, root, _exportFormat, new ExportOptions
        {
            MaximumDepth = _exportAllLevels ? null : _exportDepth,
            IncludeFiles = _exportIncludeFiles,
            MaximumRows = _exportFormat is ExportFormat.Html or ExportFormat.Pdf ? ReportRowLimit : int.MaxValue
        });
    }

    private async Task ExportAsync(ScanResult result, ScanNode root, ExportFormat format, ExportOptions options)
    {
        var (typeResource, extension) = format switch
        {
            ExportFormat.Json => ("FileJson", ".json"),
            ExportFormat.Excel => ("FileExcel", ".xlsx"),
            ExportFormat.Html => ("FileHtml", ".html"),
            ExportFormat.Pdf => ("FilePdf", ".pdf"),
            _ => ("FileCsv", ".csv")
        };
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"DiskLoom-{SanitizeFileName(root.Name)}-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        picker.FileTypeChoices.Add(LocalizationService.Get(typeResource), [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var path = file.Path;
        var text = CreateReportText();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        SetBusy(true, LocalizationService.Get("Exporting"));
        try
        {
            // A sub-folder export needs its own type statistics.
            var exported = ReferenceEquals(root, result.Root)
                ? result
                : await Task.Run(() =>
                {
                    var (extensions, ages) = ScanStatistics.Build(root, DateTimeOffset.UtcNow);
                    return result with { Root = root, Extensions = extensions, Ages = ages };
                });

            ExportSummary? summary = null;
            switch (format)
            {
                case ExportFormat.Json:
                    await Task.Run(() => _exportService.ExportJsonAsync(exported, path));
                    break;
                case ExportFormat.Excel:
                    summary = await new ReportExportService().ExportXlsxAsync(exported, path, options, text);
                    break;
                case ExportFormat.Html:
                    summary = await new ReportExportService().ExportHtmlAsync(exported, path, options, text, culture);
                    break;
                case ExportFormat.Pdf:
                    var htmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DiskLoom-report-{Guid.NewGuid():N}.html");
                    try
                    {
                        summary = await new ReportExportService().ExportHtmlAsync(exported, htmlPath, options, text, culture);
                        await PdfReportPrinter.PrintAsync(htmlPath, path, $"{text.Title} – {root.FullPath}", App.WindowHandle);
                    }
                    finally
                    {
                        TryDeleteFile(htmlPath);
                    }
                    break;
                default:
                    summary = await Task.Run(() => _exportService.ExportCsvAsync(exported, path, options));
                    break;
            }

            StatusText.Text = LocalizationService.Format("ExportedTo", path);
            StatusDetailText.Text = summary is null ? string.Empty : LocalizationService.Format("ExportedRows", summary.RowsWritten);
            if (summary is { IsTruncated: true })
            {
                ShowNotification(LocalizationService.Format("ExportTruncated", summary.RowsWritten), InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static ReportText CreateReportText() => new()
    {
        Title = LocalizationService.Get("ReportTitle"),
        ScannedFolder = LocalizationService.Get("ReportScannedFolder"),
        ScanDate = LocalizationService.Get("ReportScanDate"),
        Generated = LocalizationService.Get("ReportGenerated"),
        Path = LocalizationService.Get("ReportPath"),
        Name = LocalizationService.Get("SortNameLabel"),
        Kind = LocalizationService.Get("ColumnType"),
        Folder = LocalizationService.Get("Folder"),
        File = LocalizationService.Get("File"),
        Level = LocalizationService.Get("ReportLevel"),
        Size = LocalizationService.Get("SortSizeLabel"),
        SizeOnDisk = LocalizationService.Get("SizeOnDiskLabel"),
        SizeBytes = LocalizationService.Get("ReportSizeBytes"),
        AllocatedBytes = LocalizationService.Get("ReportAllocatedBytes"),
        SizeMegabytes = LocalizationService.Get("ReportSizeMegabytes"),
        PercentOfParent = LocalizationService.Get("ColumnPercent"),
        Files = LocalizationService.Get("ColumnFiles"),
        Folders = LocalizationService.Get("ColumnFolders"),
        Modified = LocalizationService.Get("SortModifiedLabel"),
        Created = LocalizationService.Get("ColumnCreated"),
        Accessed = LocalizationService.Get("ColumnAccessed"),
        Attributes = LocalizationService.Get("ColumnAttributes"),
        FileTypes = LocalizationService.Get("FileTypesTitle"),
        Extension = LocalizationService.Get("ReportExtension"),
        Share = LocalizationService.Get("ReportShare"),
        NoExtension = LocalizationService.Get("NoExtension"),
        Truncated = LocalizationService.Get("ReportTruncated"),
        DepthAll = LocalizationService.Get("ExportAllLevels"),
        Depth = LocalizationService.Get("ReportDepth")
    };

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless.
        }
    }
}
