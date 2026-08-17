using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using DiskLoom.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace DiskLoom;

public sealed partial class MainPage : Page
{
    private const bool DisplayAllocatedMeasurements = false;
    private readonly FileSystemScanner _scanner = new();
    private readonly DuplicateFinder _duplicateFinder = new();
    private readonly SnapshotService _snapshotService = new();
    private readonly ExportService _exportService = new();
    private readonly DriveService _driveService = new();
    private readonly StorageInsightService _insightService = new();
    private readonly FileOperationService _fileOperations = new();
    private readonly UpdateService _updateService = new();
    private readonly Dictionary<string, ScanNode> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _workCancellation;
    private ScanResult? _scanResult;
    private ScanNode? _currentNode;
    private bool _loaded;
    private SortColumn _sortColumn = SortColumn.Size;
    private bool _sortDescending = true;
    private bool _syncingSortControls;
    private bool _notificationShowsIssues;
    private int _lastNonIssuesTab;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        ToolTipService.SetToolTip(UpButton, LocalizationService.Get("UpOneLevel"));
        UpdateSortIndicators();
        DrivePicker.ItemsSource = _driveService.GetDrives().Select(static drive => new DriveRow(drive)).ToArray();
        PathBox.Text = App.StartupScanPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(App.StartupScanPath))
        {
            await StartScanAsync(App.StartupScanPath);
        }

        _ = CheckForUpdatesAsync(interactive: false);
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await StartScanAsync(folder.Path);
        }
    }

    private async void ScanPath_Click(object sender, RoutedEventArgs e) => await StartScanAsync(PathBox.Text);

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await StartScanAsync(PathBox.Text);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _workCancellation?.Cancel();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is not null)
        {
            await StartScanAsync(_scanResult.Root.FullPath);
        }
    }

    private void DrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DrivePicker.SelectedItem is DriveRow row && row.Drive.IsReady)
        {
            PathBox.Text = row.Drive.Name;
        }
    }

    private async Task StartScanAsync(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            ShowNotification(LocalizationService.Get("EnterScanPath"), InfoBarSeverity.Warning);
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
            return;
        }

        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _workCancellation = cancellation;
        SetBusy(true, LocalizationService.Get("StartingScan"));
        ResetResults();
        PathBox.Text = path;
        var progress = new Progress<ScanProgress>(value =>
        {
            StatusText.Text = value.CurrentPath;
            StatusDetailText.Text = LocalizationService.Format("ScanProgressDetail", value.FilesScanned, ByteFormatter.Format(value.BytesScanned), value.Elapsed);
        });

        try
        {
            var result = await _scanner.ScanAsync(path, new ScanOptions
            {
                CalculateAllocatedSize = true,
                Parallelism = Math.Clamp(Environment.ProcessorCount, 2, 32),
                FollowReparsePoints = false
            }, progress, cancellation.Token);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _scanResult = result;
            _nodesByPath.Clear();
            foreach (var node in result.Root.DescendantsAndSelf())
            {
                _nodesByPath[node.FullPath] = node;
            }

            PopulateScan(result);
            ShowNode(result.Root);
            StatusText.Text = LocalizationService.Format("ScanComplete", result.Root.FullPath);
            StatusDetailText.Text = LocalizationService.Format("ScanCompleteDetail", result.Root.FileCount, ByteFormatter.Format(result.Root.Size), result.Duration);
            if (result.Issues.Count > 0)
            {
                ShowNotification(DescribeIssues(result.Issues), InfoBarSeverity.Warning, showIssuesAction: true);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = LocalizationService.Get("ScanCanceled");
            StatusDetailText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationService.Get("ScanFailed");
            StatusDetailText.Text = string.Empty;
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_workCancellation, cancellation))
            {
                SetBusy(false);
                cancellation.Dispose();
                _workCancellation = null;
            }
        }
    }

    private void PopulateScan(ScanResult result)
    {
        DirectoryTree.RootNodes.Clear();
        var rootNode = CreateTreeNode(result.Root);
        rootNode.IsExpanded = true;
        DirectoryTree.RootNodes.Add(rootNode);
        PopulateTreeChildren(rootNode);
        TreeCountText.Text = $"{result.Root.FolderCount + 1:N0}";

        const bool displayAllocated = DisplayAllocatedMeasurements;
        LargestFilesList.ItemsSource = result.Root.Files()
            .OrderByDescending(file => GetMeasure(file, displayAllocated))
            .Take(1000)
            .Select(file => new NodeRow(file, displayAllocated))
            .ToArray();
        ExtensionsList.ItemsSource = result.Extensions.Take(500).Select(static item => new ExtensionRow(item)).ToArray();
        AgeList.ItemsSource = result.Ages.Select(static item => new AgeRow(item)).ToArray();
        InsightsList.ItemsSource = _insightService.Analyze(result).Select(static item => new InsightRow(item)).ToArray();
        IssuesList.ItemsSource = result.Issues.Select(static item => new IssueRow(item)).ToArray();
        IssuesHeaderText.Text = result.Issues.Count == 0
            ? LocalizationService.Get("NoIssues")
            : LocalizationService.Format("IssuesRecorded", result.Issues.Count);

        RefreshButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        SaveSnapshotButton.IsEnabled = true;
        CompareButton.IsEnabled = true;
    }

    private TreeViewNode CreateTreeNode(ScanNode node)
    {
        const bool displayAllocated = DisplayAllocatedMeasurements;
        return new TreeViewNode
        {
            Content = new NodeRow(node, displayAllocated),
            HasUnrealizedChildren = node.Children.Any(static child => child.IsDirectory)
        };
    }

    private void PopulateTreeChildren(TreeViewNode treeNode)
    {
        if (treeNode.Content is not NodeRow row)
        {
            return;
        }

        treeNode.Children.Clear();
        foreach (var child in row.Source.Children.Where(static child => child.IsDirectory))
        {
            treeNode.Children.Add(CreateTreeNode(child));
        }
        treeNode.HasUnrealizedChildren = false;
    }

    private void DirectoryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.HasUnrealizedChildren)
        {
            PopulateTreeChildren(args.Node);
        }
    }

    private void DirectoryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (ResolveTreeRow(sender, args.InvokedItem) is { } row)
        {
            ShowNode(row.Source);
        }
    }

    private void DirectoryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is NodeRow row)
        {
            ShowNode(row.Source);
        }
    }

    private void DirectoryTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResolveTreeRow(DirectoryTree, DirectoryTree.SelectedNode) is { } row)
        {
            ShowNode(row.Source);
            e.Handled = true;
        }
    }

    private static NodeRow? ResolveTreeRow(TreeView tree, object? item) => item switch
    {
        NodeRow row => row,
        TreeViewNode { Content: NodeRow row } => row,
        _ => tree.SelectedNode?.Content as NodeRow
    };

    private void ShowNode(ScanNode node)
    {
        _currentNode = node;
        PathBox.Text = node.FullPath;
        LogicalSizeText.Text = ByteFormatter.Format(node.Size);
        AllocatedSizeText.Text = ByteFormatter.Format(node.AllocatedSize);
        FilesText.Text = node.FileCount.ToString("N0");
        FoldersText.Text = node.FolderCount.ToString("N0");
        UpButton.IsEnabled = _scanResult is not null && !node.FullPath.Equals(_scanResult.Root.FullPath, StringComparison.OrdinalIgnoreCase);
        ApplyFilter();
        RenderTreemap();
    }

    private void ApplyFilter()
    {
        if (!_loaded)
        {
            return;
        }

        if (_currentNode is null)
        {
            ChildrenList.ItemsSource = null;
            return;
        }

        var query = FilterBox.Text.Trim();
        IEnumerable<ScanNode> nodes = RecursiveCheckBox.IsChecked == true
            ? _currentNode.DescendantsAndSelf().Skip(1)
            : _currentNode.Children;
        if (!string.IsNullOrWhiteSpace(query))
        {
            nodes = nodes.Where(node =>
                node.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                node.FullPath.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        nodes = SortNodes(nodes);

        const bool displayAllocated = DisplayAllocatedMeasurements;
        ChildrenList.ItemsSource = nodes
            .Where(static node => node.IsDirectory ? Directory.Exists(node.FullPath) : File.Exists(node.FullPath))
            .Take(100_000)
            .Select(node => new NodeRow(node, displayAllocated))
            .ToArray();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded) ApplyFilter();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _syncingSortControls || SortBox.SelectedIndex < 0)
        {
            return;
        }

        _sortColumn = (SortColumn)SortBox.SelectedIndex;
        _sortDescending = _sortColumn != SortColumn.Name;
        UpdateSortIndicators();
        ApplyFilter();
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        UpdateSortIndicators();
        ApplyFilter();
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SortColumn>(tag, out var selectedColumn))
        {
            return;
        }

        if (_sortColumn == selectedColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = selectedColumn;
            _sortDescending = selectedColumn != SortColumn.Name;
        }

        _syncingSortControls = true;
        SortBox.SelectedIndex = (int)_sortColumn;
        _syncingSortControls = false;
        UpdateSortIndicators();
        ApplyFilter();
    }

    private IEnumerable<ScanNode> SortNodes(IEnumerable<ScanNode> nodes) => (_sortColumn, _sortDescending) switch
    {
        (SortColumn.Name, false) => nodes.OrderBy(static node => node.Name, StringComparer.CurrentCultureIgnoreCase),
        (SortColumn.Name, true) => nodes.OrderByDescending(static node => node.Name, StringComparer.CurrentCultureIgnoreCase),
        (SortColumn.Size, false) => nodes.OrderBy(static node => node.Size),
        (SortColumn.Size, true) => nodes.OrderByDescending(static node => node.Size),
        (SortColumn.Allocated, false) => nodes.OrderBy(static node => node.AllocatedSize),
        (SortColumn.Allocated, true) => nodes.OrderByDescending(static node => node.AllocatedSize),
        (SortColumn.Modified, false) => nodes.OrderBy(static node => node.LastWriteUtc),
        (SortColumn.Modified, true) => nodes.OrderByDescending(static node => node.LastWriteUtc),
        (SortColumn.FileCount, false) => nodes.OrderBy(static node => node.FileCount),
        _ => nodes.OrderByDescending(static node => node.FileCount)
    };

    private void UpdateSortIndicators()
    {
        if (NameHeaderText is null)
        {
            return;
        }

        NameHeaderText.Text = SortHeader(LocalizationService.Get("SortNameLabel"), SortColumn.Name);
        SizeHeaderText.Text = SortHeader(LocalizationService.Get("SortSizeLabel"), SortColumn.Size);
        AllocatedHeaderText.Text = SortHeader(LocalizationService.Get("SortAllocatedLabel"), SortColumn.Allocated);
        ModifiedHeaderText.Text = SortHeader(LocalizationService.Get("SortModifiedLabel"), SortColumn.Modified);
        SortDirectionButton.Content = _sortDescending ? "↓" : "↑";
        ToolTipService.SetToolTip(SortDirectionButton, LocalizationService.Get(_sortDescending ? "SortDescendingTip" : "SortAscendingTip"));
    }

    private string SortHeader(string label, SortColumn column) => _sortColumn == column
        ? $"{label} {(_sortDescending ? "↓" : "↑")}"
        : label;

    private void RecursiveCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded) ApplyFilter();
    }

    private void ChildrenList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The context menu reads the selected row directly; this keeps right-click and keyboard selection aligned.
    }

    private void ChildrenList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is not NodeRow row)
        {
            return;
        }

        if (row.Source.IsDirectory)
        {
            ShowNode(row.Source);
        }
        else
        {
            _fileOperations.ShowInExplorer(row.Source.FullPath);
        }
    }

    private void AnyFileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LargestFilesList.SelectedItem is NodeRow row)
        {
            _fileOperations.ShowInExplorer(row.Source.FullPath);
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNode is null || _scanResult is null)
        {
            return;
        }

        var parentPath = Directory.GetParent(_currentNode.FullPath)?.FullName;
        if (parentPath is not null && _nodesByPath.TryGetValue(parentPath, out var parent))
        {
            ShowNode(parent);
        }
        else
        {
            ShowNode(_scanResult.Root);
        }
    }

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTreemap();

    private void TreemapCanvas_Loaded(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(RenderTreemap);

    private void TreemapCanvas_LayoutUpdated(object? sender, object e)
    {
        if (_loaded &&
            WorkspaceTabs.SelectedIndex == 1 &&
            TreemapCanvas.Children.Count == 0 &&
            TreemapCanvas.ActualWidth >= 20 &&
            TreemapCanvas.ActualHeight >= 20 &&
            _currentNode?.Children.Any(node => GetMeasure(node, DisplayAllocatedMeasurements) > 0) == true)
        {
            RenderTreemap();
        }
    }

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && WorkspaceTabs.SelectedIndex is >= 0 and not 7)
        {
            _lastNonIssuesTab = WorkspaceTabs.SelectedIndex;
        }

        if (_loaded && WorkspaceTabs.SelectedIndex == 1)
        {
            await Task.Delay(50);
            RenderTreemap();
        }
    }

    private void RenderTreemap()
    {
        TreemapCanvas.Children.Clear();
        if (_currentNode is null || TreemapCanvas.ActualWidth < 20 || TreemapCanvas.ActualHeight < 20)
        {
            return;
        }

        const bool displayAllocated = DisplayAllocatedMeasurements;
        var nodes = _currentNode.Children
            .Where(node => GetMeasure(node, displayAllocated) > 0)
            .OrderByDescending(node => GetMeasure(node, displayAllocated))
            .Take(250)
            .ToArray();
        var tiles = Squarify(nodes, TreemapCanvas.ActualWidth, TreemapCanvas.ActualHeight, displayAllocated);
        foreach (var tile in tiles)
        {
            var border = new Border
            {
                Width = Math.Max(0, tile.Width - 2),
                Height = Math.Max(0, tile.Height - 2),
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(GetTileColor(tile.Node)),
                Tag = tile.Node
            };

            if (tile.Width >= 70 && tile.Height >= 42)
            {
                border.Child = new StackPanel
                {
                    Padding = new Thickness(7, 5, 7, 5),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = tile.Node.Name,
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = ByteFormatter.Format(GetMeasure(tile.Node, displayAllocated)),
                            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                            FontSize = 11
                        }
                    }
                };
            }

            ToolTipService.SetToolTip(border, $"{tile.Node.FullPath}\n{ByteFormatter.Format(GetMeasure(tile.Node, displayAllocated))}");
            border.DoubleTapped += TreemapTile_DoubleTapped;
            Canvas.SetLeft(border, tile.X);
            Canvas.SetTop(border, tile.Y);
            TreemapCanvas.Children.Add(border);
        }
    }

    private void TreemapTile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: ScanNode node })
        {
            if (node.IsDirectory)
            {
                ShowNode(node);
            }
            else
            {
                _fileOperations.ShowInExplorer(node.FullPath);
            }
        }
    }

    private async void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null)
        {
            ShowNotification(LocalizationService.Get("ScanBeforeDuplicates"), InfoBarSeverity.Warning);
            return;
        }

        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _workCancellation = cancellation;
        SetBusy(true, LocalizationService.Get("PreparingDuplicates"));
        var minimumBytes = (long)Math.Max(0, double.IsNaN(DuplicateMinimumBox.Value) ? 1 : DuplicateMinimumBox.Value) * 1024 * 1024;
        var progress = new Progress<DuplicateProgress>(value =>
        {
            var phase = LocalizationService.Get(value.Phase == "Sampling" ? "DuplicatePhaseSampling" : "DuplicatePhaseVerifying");
            StatusText.Text = LocalizationService.Format("DuplicateProgress", phase, value.CurrentPath);
            StatusDetailText.Text = $"{value.Processed:N0} / {value.Total:N0}";
            WorkProgress.IsIndeterminate = value.Total == 0;
            WorkProgress.Value = value.Total == 0 ? 0 : value.Processed * 100d / value.Total;
        });

        try
        {
            var result = await _duplicateFinder.FindAsync(_scanResult.Root, minimumBytes, progress, cancellation.Token);
            var rows = result.Groups.SelectMany((group, index) => group.Files.Select(file => new DuplicateRow(index + 1, group, file))).ToArray();
            DuplicatesList.ItemsSource = rows;
            DuplicateSummaryText.Text = LocalizationService.Format("DuplicateSummary", result.Groups.Count, rows.Length, ByteFormatter.Format(result.ReclaimableSize));
            StatusText.Text = LocalizationService.Get("DuplicateComplete");
            StatusDetailText.Text = LocalizationService.Format("VerifiedGroups", result.Groups.Count);
            if (result.Issues.Count > 0)
            {
                ShowNotification(LocalizationService.Format("DuplicateHashIssues", result.Issues.Count), InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = LocalizationService.Get("DuplicateCanceled");
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_workCancellation, cancellation))
            {
                SetBusy(false);
                cancellation.Dispose();
                _workCancellation = null;
            }
        }
    }

    private void ShowDuplicateInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (DuplicatesList.SelectedItem is DuplicateRow row)
        {
            _fileOperations.ShowInExplorer(row.Path);
        }
    }

    private void DuplicateList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ShowDuplicateInExplorer_Click(sender, e);

    private void InsightList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (InsightsList.SelectedItem is InsightRow row)
        {
            _fileOperations.ShowInExplorer(row.Path);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"DiskLoom-{SanitizeFileName(_scanResult.Root.Name)}-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        picker.FileTypeChoices.Add(LocalizationService.Get("FileCsv"), [".csv"]);
        picker.FileTypeChoices.Add(LocalizationService.Get("FileJson"), [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            SetBusy(true, LocalizationService.Get("Exporting"));
            if (file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                await _exportService.ExportJsonAsync(_scanResult, file.Path);
            }
            else
            {
                await _exportService.ExportCsvAsync(_scanResult, file.Path);
            }
            StatusText.Text = LocalizationService.Format("ExportedTo", file.Path);
            StatusDetailText.Text = string.Empty;
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

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"DiskLoom-{SanitizeFileName(_scanResult.Root.Name)}-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        picker.FileTypeChoices.Add(LocalizationService.Get("FileSnapshot"), [".diskloom"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            SetBusy(true, LocalizationService.Get("SavingSnapshot"));
            await _snapshotService.SaveAsync(_scanResult, file.Path);
            StatusText.Text = LocalizationService.Format("SnapshotSaved", file.Path);
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

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".diskloom");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            SetBusy(true, LocalizationService.Get("ComparingSnapshots"));
            var older = await _snapshotService.LoadAsync(file.Path);
            var current = _snapshotService.Create(_scanResult);
            var changes = _snapshotService.Compare(older, current);
            ChangesList.ItemsSource = changes.Select(static change => new ChangeRow(change)).ToArray();
            ChangesHeaderText.Text = LocalizationService.Format("ChangesSince", changes.Count, older.CreatedUtc.LocalDateTime, older.RootPath);
            WorkspaceTabs.SelectedIndex = 6;
            StatusText.Text = LocalizationService.Get("ComparisonComplete");
            StatusDetailText.Text = LocalizationService.Format("ChangedPaths", changes.Count);
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

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is NodeRow row)
        {
            _fileOperations.ShowInExplorer(row.Source.FullPath);
        }
    }

    private void CopySelectedPath_Click(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is NodeRow row)
        {
            CopyText(row.Source.FullPath);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => CopyText(_currentNode?.FullPath ?? PathBox.Text);

    private void ShowIssueInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (IssuesList.SelectedItem is not IssueRow row)
        {
            return;
        }

        var path = row.Path;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                path = parent;
            }
        }
        _fileOperations.ShowInExplorer(path);
    }

    private void CopyIssuePath_Click(object sender, RoutedEventArgs e)
    {
        if (IssuesList.SelectedItem is IssueRow row)
        {
            CopyText(row.Path);
        }
    }

    private void NotificationActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_notificationShowsIssues)
        {
            return;
        }

        if (WorkspaceTabs.SelectedIndex != 7)
        {
            _lastNonIssuesTab = Math.Max(0, WorkspaceTabs.SelectedIndex);
        }
        WorkspaceTabs.SelectedIndex = 7;
        NotificationBar.IsOpen = false;
        IssuesList.Focus(FocusState.Programmatic);
    }

    private void IssuesBackButton_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceTabs.SelectedIndex = _lastNonIssuesTab is >= 0 and < 7 ? _lastNonIssuesTab : 0;
    }

    private async void RecycleSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is not NodeRow row)
        {
            return;
        }

        var dialog = CreateDialog(
            LocalizationService.Get("RecycleTitle"),
            row.Source.FullPath,
            LocalizationService.Get("Recycle"),
            LocalizationService.Get("Cancel"),
            ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _fileOperations.RecycleAsync(row.Source.FullPath);
            ApplyFilter();
            ShowNotification(LocalizationService.Get("Recycled"), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is not NodeRow row)
        {
            return;
        }

        var dialog = CreateDialog(
            LocalizationService.Get("DeleteTitle"),
            LocalizationService.Format("DeleteWarning", row.Source.FullPath),
            LocalizationService.Get("DeletePermanently"),
            LocalizationService.Get("Cancel"),
            ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _fileOperations.DeletePermanentlyAsync(row.Source.FullPath);
            ApplyFilter();
            ShowNotification(LocalizationService.Get("Deleted"), InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(interactive: true);

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        try
        {
            var configurationPath = Path.Combine(AppContext.BaseDirectory, "update-config.json");
            var configuration = await UpdateService.LoadConfigurationAsync(configurationPath);
            if (!interactive && (!configuration.CheckOnStartup || !ShouldRunScheduledUpdateCheck(configuration.CheckIntervalHours)))
            {
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
            var result = await _updateService.CheckAsync(configuration, currentVersion);
            RecordUpdateCheck();
            if (!result.IsConfigured)
            {
                if (interactive)
                {
                    await CreateDialog(
                        LocalizationService.Get("UpdatesTitle"),
                        LocalizationService.Format("UpdateNotConfigured", configurationPath),
                        LocalizationService.Get("Ok")).ShowAsync();
                }
                return;
            }

            if (!result.IsUpdateAvailable || result.Manifest is null)
            {
                if (interactive)
                {
                    await CreateDialog(LocalizationService.Get("UpdatesTitle"), LocalizationService.Get("UpdateCurrent"), LocalizationService.Get("Ok")).ShowAsync();
                }
                return;
            }

            var availableVersion = result.Manifest.Version;
            var canInstallDirectly = result.Manifest.HasVerifiableInstaller &&
                                     !string.IsNullOrWhiteSpace(configuration.PublisherCertificateSha256);
            var prompt = CreateDialog(
                LocalizationService.Format("UpdateAvailable", availableVersion),
                string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotes)
                    ? LocalizationService.Get(canInstallDirectly ? "UpdatePrompt" : "OpenGitHubPrompt")
                    : result.Manifest.ReleaseNotes,
                LocalizationService.Get(canInstallDirectly ? "DownloadInstall" : "OpenGitHubRelease"),
                LocalizationService.Get("Later"),
                result.Manifest.Mandatory ? ContentDialogButton.Primary : ContentDialogButton.Close);
            if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!canInstallDirectly)
            {
                var releaseUrl = string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotesUrl)
                    ? "https://github.com/Naxterra/DiskLoom/releases/latest"
                    : result.Manifest.ReleaseNotesUrl;
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                return;
            }

            SetBusy(true, LocalizationService.Get("DownloadingUpdate"), indeterminate: false);
            var progress = new Progress<double>(value => WorkProgress.Value = value * 100);
            var installer = await _updateService.DownloadAndVerifyAsync(result.Manifest, configuration, progress);
            StatusText.Text = LocalizationService.Get("StartingInstaller");
            _updateService.StartInstaller(installer);
            App.Window.Close();
        }
        catch (Exception exception)
        {
            if (interactive)
            {
                ShowNotification(LocalizationService.Format("UpdateFailed", exception.Message), InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (_workCancellation is null)
            {
                SetBusy(false);
            }
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        await CreateDialog(
            $"DiskLoom {version}",
            LocalizationService.Format("AboutContent", RuntimeInformation.FrameworkDescription, CreatorIdentity.AboutLine),
            LocalizationService.Get("Close")).ShowAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var languageBox = new ComboBox
        {
            Header = LocalizationService.Get("SettingsLanguage"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "English", "Deutsch" },
            SelectedIndex = App.CurrentLanguage == "de-DE" ? 1 : 0
        };
        var content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 320,
            Children =
            {
                languageBox,
                new TextBlock
                {
                    Text = LocalizationService.Get("SettingsRestartHint"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                }
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("SettingsTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Apply"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var language = languageBox.SelectedIndex == 1 ? "de-DE" : "en-US";
            App.RestartWithLanguage(language, _scanResult?.Root.FullPath);
        }
    }

    private void ResetResults()
    {
        DirectoryTree.RootNodes.Clear();
        ChildrenList.ItemsSource = null;
        LargestFilesList.ItemsSource = null;
        ExtensionsList.ItemsSource = null;
        AgeList.ItemsSource = null;
        DuplicatesList.ItemsSource = null;
        InsightsList.ItemsSource = null;
        ChangesList.ItemsSource = null;
        IssuesList.ItemsSource = null;
        TreemapCanvas.Children.Clear();
        LogicalSizeText.Text = "—";
        AllocatedSizeText.Text = "—";
        FilesText.Text = "—";
        FoldersText.Text = "—";
        TreeCountText.Text = LocalizationService.Get("Scanning");
        _scanResult = null;
        _currentNode = null;
        _nodesByPath.Clear();
        RefreshButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        SaveSnapshotButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        UpButton.IsEnabled = false;
        NotificationActionButton.Visibility = Visibility.Collapsed;
        _notificationShowsIssues = false;
    }

    private void SetBusy(bool busy, string? message = null, bool indeterminate = true)
    {
        CancelButton.IsEnabled = busy && _workCancellation is not null;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        WorkProgress.IsIndeterminate = busy && indeterminate;
        if (!busy)
        {
            WorkProgress.Value = 0;
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private void ShowNotification(string message, InfoBarSeverity severity, bool showIssuesAction = false)
    {
        NotificationBar.Message = message;
        NotificationBar.Severity = severity;
        _notificationShowsIssues = showIssuesAction;
        NotificationActionButton.Visibility = showIssuesAction ? Visibility.Visible : Visibility.Collapsed;
        NotificationBar.IsOpen = true;
    }

    private static string DescribeIssues(IReadOnlyList<ScanIssue> issues)
    {
        var accessDenied = issues.Count(static issue => issue.ErrorType is nameof(UnauthorizedAccessException) or "SecurityException");
        var missing = issues.Count(static issue => issue.ErrorType is nameof(FileNotFoundException) or nameof(DirectoryNotFoundException));
        var inputOutput = issues.Count(static issue => issue.ErrorType == nameof(IOException));
        var other = issues.Count - accessDenied - missing - inputOutput;
        var details = new List<string>();
        if (accessDenied > 0) details.Add(LocalizationService.Format("IssueAccessDenied", accessDenied));
        if (missing > 0) details.Add(LocalizationService.Format("IssueMissing", missing));
        if (inputOutput > 0) details.Add(LocalizationService.Format("IssueIo", inputOutput));
        if (other > 0) details.Add(LocalizationService.Format("IssueOther", other));
        return LocalizationService.Format("IssueSummary", issues.Count, string.Join(", ", details));
    }

    private ContentDialog CreateDialog(
        string title,
        string content,
        string primaryText,
        string? closeText = null,
        ContentDialogButton defaultButton = ContentDialogButton.Primary) => new()
    {
        XamlRoot = XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primaryText,
        CloseButtonText = closeText ?? string.Empty,
        DefaultButton = defaultButton
    };

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static long GetMeasure(ScanNode node, bool allocated) => allocated ? node.AllocatedSize : node.Size;

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static Color GetTileColor(ScanNode node)
    {
        Color[] palette =
        [
            Color.FromArgb(255, 0, 120, 212),
            Color.FromArgb(255, 16, 124, 16),
            Color.FromArgb(255, 136, 23, 152),
            Color.FromArgb(255, 202, 80, 16),
            Color.FromArgb(255, 0, 99, 177),
            Color.FromArgb(255, 177, 70, 194),
            Color.FromArgb(255, 0, 153, 188),
            Color.FromArgb(255, 73, 130, 5),
            Color.FromArgb(255, 105, 121, 126)
        ];
        var key = node.IsDirectory ? node.Name : node.Extension;
        return palette[(key.GetHashCode(StringComparison.OrdinalIgnoreCase) & int.MaxValue) % palette.Length];
    }

    private static IReadOnlyList<TreemapTile> Squarify(IReadOnlyList<ScanNode> nodes, double width, double height, bool allocated)
    {
        if (nodes.Count == 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        var total = nodes.Sum(node => (double)GetMeasure(node, allocated));
        if (total <= 0)
        {
            return [];
        }

        var items = nodes.Select(node => new AreaItem(node, GetMeasure(node, allocated) / total * width * height)).ToList();
        var result = new List<TreemapTile>(items.Count);
        var remaining = new LayoutRect(0, 0, width, height);
        var row = new List<AreaItem>();

        while (items.Count > 0)
        {
            var next = items[0];
            var side = Math.Max(1, Math.Min(remaining.Width, remaining.Height));
            if (row.Count == 0 || WorstAspect(row.Append(next), side) <= WorstAspect(row, side))
            {
                row.Add(next);
                items.RemoveAt(0);
            }
            else
            {
                LayoutRow(row, ref remaining, result);
                row.Clear();
            }
        }

        if (row.Count > 0)
        {
            LayoutRow(row, ref remaining, result);
        }
        return result;
    }

    private static double WorstAspect(IEnumerable<AreaItem> items, double side)
    {
        var areas = items.Select(static item => item.Area).Where(static area => area > 0).ToArray();
        if (areas.Length == 0)
        {
            return double.MaxValue;
        }
        var sum = areas.Sum();
        var sideSquared = side * side;
        return Math.Max(sideSquared * areas.Max() / (sum * sum), (sum * sum) / (sideSquared * areas.Min()));
    }

    private static void LayoutRow(IReadOnlyList<AreaItem> row, ref LayoutRect remaining, ICollection<TreemapTile> result)
    {
        var rowArea = row.Sum(static item => item.Area);
        if (remaining.Width >= remaining.Height)
        {
            var stripWidth = remaining.Height <= 0 ? 0 : rowArea / remaining.Height;
            var y = remaining.Y;
            foreach (var item in row)
            {
                var itemHeight = stripWidth <= 0 ? 0 : item.Area / stripWidth;
                result.Add(new TreemapTile(item.Node, remaining.X, y, stripWidth, itemHeight));
                y += itemHeight;
            }
            remaining = new LayoutRect(remaining.X + stripWidth, remaining.Y, Math.Max(0, remaining.Width - stripWidth), remaining.Height);
        }
        else
        {
            var stripHeight = remaining.Width <= 0 ? 0 : rowArea / remaining.Width;
            var x = remaining.X;
            foreach (var item in row)
            {
                var itemWidth = stripHeight <= 0 ? 0 : item.Area / stripHeight;
                result.Add(new TreemapTile(item.Node, x, remaining.Y, itemWidth, stripHeight));
                x += itemWidth;
            }
            remaining = new LayoutRect(remaining.X, remaining.Y + stripHeight, remaining.Width, Math.Max(0, remaining.Height - stripHeight));
        }
    }

    private static bool ShouldRunScheduledUpdateCheck(int intervalHours)
    {
        try
        {
            var path = GetUpdateStatePath();
            return !File.Exists(path) ||
                   !DateTimeOffset.TryParse(File.ReadAllText(path), out var lastCheck) ||
                   DateTimeOffset.UtcNow - lastCheck >= TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 24 * 30));
        }
        catch
        {
            return true;
        }
    }

    private static void RecordUpdateCheck()
    {
        try
        {
            var path = GetUpdateStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // A read-only profile should not prevent update checks.
        }
    }

    private static string GetUpdateStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskLoom",
        "last-update-check.txt");

    private sealed record AreaItem(ScanNode Node, double Area);
    private readonly record struct LayoutRect(double X, double Y, double Width, double Height);
    private sealed record TreemapTile(ScanNode Node, double X, double Y, double Width, double Height);

    private enum SortColumn
    {
        Size = 0,
        Allocated = 1,
        Name = 2,
        Modified = 3,
        FileCount = 4
    }
}
