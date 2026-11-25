using System.Diagnostics;
using System.Reflection;
using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using DiskLoom.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace DiskLoom;

public sealed partial class MainPage : Page
{
    private const double DefaultTreePaneWidth = 420;
    private readonly FileSystemScanner _scanner = new();
    private readonly DuplicateFinder _duplicateFinder = new();
    private readonly SnapshotService _snapshotService = new();
    private readonly ExportService _exportService = new();
    private readonly DriveService _driveService = new();
    private readonly StorageInsightService _insightService = new();
    private readonly FileOperationService _fileOperations = new();
    private readonly UpdateService _updateService = new();
    private readonly Dictionary<string, ScanNode> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScanNode?> _parentsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScanPresentation> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewNode> _treeNodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<TreeViewNode> _treeNodesBeingPopulated = [];
    private readonly List<ScanNode> _navigationHistory = [];
    private MenuFlyout? _treemapMenu;
    private IReadOnlyList<DriveSummary> _drives = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterDebounceTimer;
    private CancellationTokenSource? _workCancellation;
    private ScanResult? _scanResult;
    private ScanNode? _currentNode;
    private FolderTreeRow? _treeContextRow;
    private ListView? _fileMenuList;
    private bool _loaded;
    private ResultSortColumn _sortColumn = ResultSortColumn.Size;
    private bool _sortDescending = true;
    private bool _syncingSortControls;
    private bool _syncingDrivePicker;
    private bool _syncingTreeSelection;
    private bool _updatingTree;
    private bool _notificationShowsIssues;
    private bool _removalInProgress;
    private object? _lastNonIssuesTab;
    private int _navigationIndex = -1;
    private long _scanRequestId;

    public ResultColumnLayout ResultColumnWidths { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        // Attach the shared menu to row containers so its target is the row under the pointer.
        ChildrenList.ContextFlyout = null;
        foreach (var list in new[] { ChildrenList, LargestFilesList, InsightsList, DuplicatesList, SearchResultsList })
        {
            list.ContainerContentChanging += FileList_ContainerContentChanging;
            list.AddHandler(KeyDownEvent, new KeyEventHandler(FileList_KeyDown), handledEventsToo: true);
        }
        DirectoryTree.AddHandler(KeyDownEvent, new KeyEventHandler(DirectoryTree_KeyDown), handledEventsToo: true);
        DirectoryTree.ContextRequested += DirectoryTree_ContextRequested;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _displayAllocated = App.Settings.DisplayAllocatedSize;
        AllocatedModeButton.IsChecked = _displayAllocated;
        TreePaneColumn.Width = new GridLength(Math.Clamp(App.Settings.TreePaneWidth, TreePaneColumn.MinWidth, TreePaneColumn.MaxWidth));
        ResultColumnWidths.LoadWidths(App.Settings.ColumnWidths);

        ToolTipService.SetToolTip(BackButton, LocalizationService.Get("Back"));
        ToolTipService.SetToolTip(ForwardButton, LocalizationService.Get("Forward"));
        ToolTipService.SetToolTip(UpButton, LocalizationService.Get("UpOneLevel"));
        ToolTipService.SetToolTip(EditPathButton, LocalizationService.Get("EditPath"));
        ToolTipService.SetToolTip(TreeSplitter, LocalizationService.Get("TreeSplitterTip"));
        ApplyResultColumns(ResultColumns.Load());
        InitializeFeatureControls();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackButton, LocalizationService.Get("Back"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ForwardButton, LocalizationService.Get("Forward"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UpButton, LocalizationService.Get("UpOneLevel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(EditPathButton, LocalizationService.Get("EditPath"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TreeSplitter, LocalizationService.Get("TreeSplitterTip"));
        UpdateSortIndicators();
        RefreshDrives();
        PathBox.Text = App.StartupScanPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(App.StartupScanPath))
        {
            await StartScanAsync(App.StartupScanPath);
        }

        var runningVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        _ = Task.Run(() => CleanLeftovers(runningVersion));
        _ = CheckForUpdatesAsync(interactive: false);
    }

    internal void RefreshDrives()
    {
        if (!_loaded)
        {
            return;
        }

        _drives = _driveService.GetDrives();
        var readyRoots = _drives.Where(static drive => drive.IsReady).Select(static drive => drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleCached = _scanCache.Keys
            .Where(key => Path.GetPathRoot(key) is { } root && !readyRoots.Contains(root))
            .ToArray();
        foreach (var key in staleCached)
        {
            _scanCache.Remove(key);
            _scanCacheOrder.RemoveAll(entry => entry.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        _syncingDrivePicker = true;
        try
        {
            DrivePicker.ItemsSource = _drives.Select(static drive => new DriveRow(drive)).ToArray();
            if (_currentNode is not null || _scanResult is not null)
            {
                SelectDriveForPath(_currentNode?.FullPath ?? _scanResult!.Root.FullPath);
            }
        }
        finally
        {
            _syncingDrivePicker = false;
        }

        if (_scanResult is null && _workCancellation is null)
        {
            RebuildDirectoryTree(result: null);
        }
    }

    private void DrivePicker_DropDownOpened(object? sender, object e) => RefreshDrives();

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = NativeFileDialogs.PickFolder(App.WindowHandle);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await StartScanAsync(folder);
        }
    }

    private async void ScanPath_Click(object sender, RoutedEventArgs e) => await StartScanAsync(PathBox.Text);

    internal Task StartExternalScanAsync(string path) => StartScanAsync(path);

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await StartScanAsync(PathBox.Text);
        }
        else if (e.Key == VirtualKey.Escape && _currentNode is not null)
        {
            e.Handled = true;
            ShowBreadcrumb();
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

    private async void DrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDrivePicker || DrivePicker.SelectedItem is not DriveRow row)
        {
            return;
        }

        if (!row.Drive.IsReady)
        {
            ShowNotification(LocalizationService.Get("NotReady"), InfoBarSeverity.Warning);
            return;
        }

        await StartScanAsync(row.Drive.Name, useCachedResult: true);
    }

    private async Task StartScanAsync(string? requestedPath, bool useCachedResult = false)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            ShowNotification(LocalizationService.Get("EnterScanPath"), InfoBarSeverity.Warning);
            return;
        }

        string path;
        try
        {
            path = ScanPaths.Normalize(requestedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
            return;
        }

        if (!Directory.Exists(WindowsPaths.ToExtended(path)))
        {
            ShowNotification(LocalizationService.Format("ScanPathMissing", path), InfoBarSeverity.Error);
            return;
        }

        var requestId = Interlocked.Increment(ref _scanRequestId);
        _workCancellation?.Cancel();

        // A drive can start a scan from TreeView.ItemInvoked or ComboBox.SelectionChanged.
        // Let that control finish its own collection update before rebuilding the tree.
        await Task.Yield();

        if (requestId != Volatile.Read(ref _scanRequestId))
        {
            return;
        }

        if (useCachedResult && _scanCache.TryGetValue(path, out var cachedResult))
        {
            ResetResults();
            ActivateScanResult(cachedResult, loadedFromCache: true);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _workCancellation = cancellation;
        SetBusy(true, LocalizationService.Get("StartingScan"));
        ResetResults();
        SelectDriveForPath(path);
        PathBox.Text = path;
        var progress = new Progress<ScanProgress>(value =>
        {
            if (requestId != Volatile.Read(ref _scanRequestId))
            {
                return;
            }
            StatusText.Text = value.CurrentPath;
            StatusDetailText.Text = LocalizationService.Format("ScanProgressDetail", value.FilesScanned, ByteFormatter.Format(value.BytesScanned), value.Elapsed);
            if (_currentNode is null)
            {
                LogicalSizeText.Text = ByteFormatter.Format(value.BytesScanned);
                FilesText.Text = value.FilesScanned.ToString("N0");
                // The final count excludes the scanned folder itself.
                FoldersText.Text = Math.Max(0, value.DirectoriesScanned - 1).ToString("N0");
                if (value.TopLevel is { } topLevel)
                {
                    ShowLiveResults(topLevel);
                }
            }
        });

        try
        {
            var result = await _scanner.ScanAsync(path, new ScanOptions
            {
                CalculateAllocatedSize = true,
                Parallelism = Math.Clamp(Environment.ProcessorCount, 2, 32),
                FollowReparsePoints = false,
                ExcludePatterns = App.Settings.ExcludeSystemFolders ? ScanOptions.SystemProtectedFolderPatterns : []
            }, progress, cancellation.Token);
            var presentation = await Task.Run(() => CreatePresentation(result), cancellation.Token);

            if (cancellation.IsCancellationRequested || requestId != Volatile.Read(ref _scanRequestId))
            {
                return;
            }

            CacheScan(path, presentation);
            ActivateScanResult(presentation, loadedFromCache: false);
            RefreshDrives();
        }
        catch (OperationCanceledException)
        {
            if (requestId == Volatile.Read(ref _scanRequestId))
            {
                ClearLiveResults();
                StatusText.Text = LocalizationService.Get("ScanCanceled");
                StatusDetailText.Text = string.Empty;
            }
        }
        catch (Exception exception)
        {
            if (requestId == Volatile.Read(ref _scanRequestId))
            {
                ClearLiveResults();
                StatusText.Text = LocalizationService.Get("ScanFailed");
                StatusDetailText.Text = string.Empty;
                ShowNotification(exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_workCancellation, cancellation))
            {
                SetBusy(false);
                _workCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private ScanPresentation CreatePresentation(ScanResult result)
    {
        var allocated = _displayAllocated;
        var largest = new PriorityQueue<ScanNode, long>(1001);
        foreach (var file in result.Root.Files())
        {
            var measure = GetMeasure(file, allocated);
            largest.Enqueue(file, measure);
            if (largest.Count > 1000)
            {
                largest.Dequeue();
            }
        }
        var topFiles = new ScanNode[largest.Count];
        for (var index = topFiles.Length - 1; index >= 0; index--)
        {
            topFiles[index] = largest.Dequeue();
        }
        return new ScanPresentation(
            result,
            topFiles,
            _insightService.Analyze(result, allocated));
    }

    private void ActivateScanResult(ScanPresentation presentation, bool loadedFromCache)
    {
        var result = presentation.Result;
        _basePresentation = presentation;
        ResetTreeFilterState();
        DisplayPresentation(presentation, preferredPath: null);
        StatusText.Text = loadedFromCache
            ? LocalizationService.Format("CachedScanLoaded", result.Root.FullPath)
            : LocalizationService.Format("ScanComplete", result.Root.FullPath);
        StatusDetailText.Text = LocalizationService.Format(
            "ScanCompleteDetail",
            result.Root.FileCount,
            ByteFormatter.Format(result.Root.Size),
            result.Duration);

        if (result.Issues.Any(static issue => !IsAccessDeniedIssue(issue)))
        {
            ShowNotification(DescribeIssues(result.Issues), InfoBarSeverity.Warning, showIssuesAction: true);
        }
    }

    // Shows a scan (or a filtered copy of it) and opens preferredPath if it is part of it.
    private void DisplayPresentation(ScanPresentation presentation, string? preferredPath)
    {
        var result = presentation.Result;
        _scanResult = result;
        ClearLiveResults();
        _nodesByPath.Clear();
        _parentsByPath.Clear();
        IndexScanNodes(result.Root, parent: null);
        // History entries point at nodes of the previous tree.
        _navigationHistory.Clear();
        _navigationIndex = -1;

        PopulateScan(presentation);
        var start = result.Root;
        for (var path = preferredPath; path is not null; path = GetParentPath(path))
        {
            if (_nodesByPath.TryGetValue(path, out var existing))
            {
                start = existing;
                break;
            }
        }
        ShowNode(start);
    }

    private void PopulateScan(ScanPresentation presentation)
    {
        var result = presentation.Result;
        RebuildDirectoryTree(result);
        TreeCountText.Text = $"{result.Root.FolderCount + 1:N0}";

        var displayAllocated = _displayAllocated;
        LargestFilesList.ItemsSource = presentation.LargestFiles
            .Select(file => new NodeRow(file, displayAllocated))
            .ToArray();
        PopulateTypesAndAge();
        InsightsList.ItemsSource = presentation.Insights.Select(static item => new InsightRow(item)).ToArray();
        IssuesList.ItemsSource = result.Issues.Select(static item => new IssueRow(item)).ToArray();
        var accessDeniedCount = result.Issues.Count(IsAccessDeniedIssue);
        var otherIssueCount = result.Issues.Count - accessDeniedCount;
        IssuesTab.Header = result.Issues.Count == 0
            ? LocalizationService.Get("IssuesTabLabel")
            : LocalizationService.Format("IssuesTabCount", result.Issues.Count);
        IssuesHeaderText.Text = result.Issues.Count == 0
            ? LocalizationService.Get("NoIssues")
            : otherIssueCount == 0
                ? LocalizationService.Format("AccessDeniedSummary", accessDeniedCount)
                : LocalizationService.Format("IssuesRecorded", result.Issues.Count);

        RefreshButton.IsEnabled = true;
        FilterButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        SaveSnapshotButton.IsEnabled = true;
        CompareButton.IsEnabled = true;
    }

    private void RebuildDirectoryTree(ScanResult? result)
    {
        _updatingTree = true;
        try
        {
            DirectoryTree.RootNodes.Clear();
            _treeNodesByPath.Clear();

            var scanVolumeRoot = result is null ? null : Path.GetPathRoot(result.Root.FullPath);
            var attachedScan = false;
            foreach (var drive in _drives)
            {
                if (result is not null && PathsEqual(drive.Name, scanVolumeRoot))
                {
                    AddScanPathToTree(result, drive);
                    attachedScan = true;
                }
                else
                {
                    DirectoryTree.RootNodes.Add(CreateTreeNode(drive.Name, node: null, drive));
                }
            }

            if (result is not null && !attachedScan)
            {
                AddScanPathToTree(result, drive: null);
            }
        }
        finally
        {
            _updatingTree = false;
        }

        if (result is null)
        {
            TreeCountText.Text = LocalizationService.Format("DriveCount", _drives.Count(static drive => drive.IsReady));
        }
    }

    private void AddScanPathToTree(ScanResult result, DriveSummary? drive)
    {
        TreeViewNode? parentTreeNode = null;
        foreach (var path in GetPathChain(result.Root.FullPath))
        {
            _nodesByPath.TryGetValue(path, out var scannedNode);
            var parentSource = (parentTreeNode?.Content as FolderTreeRow)?.Source;
            var parentSize = parentSource is null ? 0 : GetMeasure(parentSource, _displayAllocated);
            var treeNode = CreateTreeNode(path, scannedNode, parentTreeNode is null ? drive : null, parentSize);
            if (parentTreeNode is null)
            {
                DirectoryTree.RootNodes.Add(treeNode);
            }
            else
            {
                parentTreeNode.Children.Add(treeNode);
                parentTreeNode.IsExpanded = true;
            }
            parentTreeNode = treeNode;
        }

        if (parentTreeNode?.HasUnrealizedChildren == true)
        {
            PopulateTreeChildren(parentTreeNode);
        }
    }

    private TreeViewNode CreateTreeNode(string fullPath, ScanNode? node, DriveSummary? drive = null, long parentSize = 0)
    {
        var treeNode = new TreeViewNode
        {
            Content = drive is null
                ? new FolderTreeRow(fullPath, node, parentSize, _displayAllocated)
                : new FolderTreeRow(drive, node, _displayAllocated),
            HasUnrealizedChildren = node?.Children.Any(static child => child.IsDirectory) == true
        };
        _treeNodesByPath[fullPath] = treeNode;
        return treeNode;
    }

    private void PopulateTreeChildren(TreeViewNode treeNode)
    {
        if (!_treeNodesBeingPopulated.Add(treeNode))
        {
            return;
        }

        try
        {
            if (treeNode.Content is not FolderTreeRow { Source: { } source })
            {
                treeNode.HasUnrealizedChildren = false;
                return;
            }

            // WinUI raises Expanding while it is already walking the node's child
            // collection. Mark the lazy node realized before adding children and do
            // not clear that collection from inside the event; either can otherwise
            // cause a re-entrant collection modification exception.
            treeNode.HasUnrealizedChildren = false;
            if (treeNode.Children.Count == 0)
            {
                var parentSize = GetMeasure(source, _displayAllocated);
                foreach (var child in source.Children.Where(static child => child.IsDirectory))
                {
                    treeNode.Children.Add(CreateTreeNode(child.FullPath, child, parentSize: parentSize));
                }
            }
        }
        finally
        {
            _treeNodesBeingPopulated.Remove(treeNode);
        }
    }

    private void DirectoryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!_updatingTree && args.Node.HasUnrealizedChildren)
        {
            PopulateTreeChildren(args.Node);
        }
    }

    private async void DirectoryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (_updatingTree)
        {
            return;
        }

        if (ResolveTreeRow(sender, args.InvokedItem) is { } row)
        {
            if (row.Source is not null)
            {
                NavigateToNode(row.Source, addToHistory: true, synchronizeTree: false);
            }
            else if (row.Drive is { IsReady: false })
            {
                ShowNotification(LocalizationService.Get("NotReady"), InfoBarSeverity.Warning);
            }
            else
            {
                await StartScanAsync(row.FullPath, useCachedResult: true);
            }
        }
    }

    private void DirectoryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (!_updatingTree && !_syncingTreeSelection && sender.SelectedNode?.Content is FolderTreeRow { Source: { } source })
        {
            // The TreeView already owns the correct selection. Synchronizing it here
            // would set IsExpanded while WinUI is still processing SelectionChanged.
            NavigateToNode(source, addToHistory: true, synchronizeTree: false);
        }
    }

    private void DirectoryTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var container = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = container is null ? null : DirectoryTree.NodeFromContainer(container);
        if (node is null || (!node.HasUnrealizedChildren && node.Children.Count == 0))
        {
            return;
        }

        e.Handled = true;
        // DoubleTapped follows the selection events, but defer one dispatcher turn
        // so WinUI has completely finished its internal TreeView collection update.
        DispatcherQueue.TryEnqueue(() => node.IsExpanded = !node.IsExpanded);
    }

    private void DirectoryTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _treeContextRow = null;
        var container = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = container is null ? null : DirectoryTree.NodeFromContainer(container);
        if (node?.Content is not FolderTreeRow row)
        {
            e.Handled = true;
            return;
        }

        _treeContextRow = row;
        _syncingTreeSelection = true;
        try
        {
            DirectoryTree.SelectedNode = node;
        }
        finally
        {
            _syncingTreeSelection = false;
        }
    }

    private void DirectoryTree_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(DirectoryTree, out _) &&
            FindVisualParent<TreeViewItem>(args.OriginalSource as DependencyObject) is null)
        {
            _treeContextRow = null;
            args.Handled = true;
        }
    }

    private void DirectoryTreeMenu_Opening(object sender, object e)
    {
        var row = GetTreeContextRow();
        var canDelete = row is not null && CanDeleteTreeFolder(row);
        TreeRecycleMenuItem.IsEnabled = canDelete;
        TreeDeleteMenuItem.IsEnabled = canDelete;
        TreeCopyToMenuItem.IsEnabled = row?.Source is not null && Directory.Exists(WindowsPaths.ToExtended(row.FullPath));
        TreeMoveToMenuItem.IsEnabled = canDelete;
    }

    private FolderTreeRow? GetTreeContextRow() =>
        DirectoryTree.SelectedNode?.Content as FolderTreeRow ?? _treeContextRow;

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T result)
            {
                return result;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static bool CanDeleteTreeFolder(FolderTreeRow row)
    {
        if (row.Source?.IsDirectory != true || !Directory.Exists(WindowsPaths.ToExtended(row.FullPath)))
        {
            return false;
        }
        var root = Path.GetPathRoot(row.FullPath);
        return (string.IsNullOrWhiteSpace(root) ||
            !row.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) &&
            !WindowsPaths.IsVolumeRoot(row.FullPath);
    }

    private void TreeShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (GetTreeContextRow() is { } row)
        {
            _fileOperations.ShowInExplorer(row.FullPath);
        }
    }

    private void TreeCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (GetTreeContextRow() is { } row)
        {
            CopyText(row.FullPath);
        }
    }

    private async void TreeRecycle_Click(object sender, RoutedEventArgs e) =>
        await DeleteTreeTargetAsync(GetTreeContextRow(), permanently: false);

    private async void TreeDeletePermanently_Click(object sender, RoutedEventArgs e) =>
        await DeleteTreeTargetAsync(GetTreeContextRow(), permanently: true);

    private Task DeleteTreeTargetAsync(FolderTreeRow? row, bool permanently) =>
        row?.Source is { } folder && CanDeleteTreeFolder(row)
            ? DeleteTargetsAsync(new[] { new NodeRow(folder) }, permanently, fromTree: true)
            : Task.CompletedTask;

    private async Task RefreshAfterTreeDeletionAsync()
    {
        var scanRoot = _scanResult?.Root.FullPath;
        if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(WindowsPaths.ToExtended(scanRoot)))
        {
            await StartScanAsync(scanRoot);
            return;
        }

        var parent = string.IsNullOrWhiteSpace(scanRoot) ? null : GetParentPath(scanRoot);
        if (parent is not null && Directory.Exists(WindowsPaths.ToExtended(parent)))
        {
            await StartScanAsync(parent);
        }
        else
        {
            ResetResults();
        }
    }

    private static FolderTreeRow? ResolveTreeRow(TreeView tree, object? item) => item switch
    {
        FolderTreeRow row => row,
        TreeViewNode { Content: FolderTreeRow row } => row,
        _ => tree.SelectedNode?.Content as FolderTreeRow
    };

    private void ShowNode(ScanNode node) => NavigateToNode(node, addToHistory: true, synchronizeTree: true);

    private void NavigateToNode(ScanNode node, bool addToHistory, bool synchronizeTree = true)
    {
        if (addToHistory)
        {
            AddNavigationEntry(node);
        }

        _currentNode = node;
        PathBox.Text = node.FullPath;
        UpdateBreadcrumb(node);
        if (synchronizeTree)
        {
            SynchronizeTreeToNode(node);
        }
        LogicalSizeText.Text = ByteFormatter.Format(node.Size);
        AllocatedSizeText.Text = ByteFormatter.Format(node.AllocatedSize);
        FilesText.Text = node.FileCount.ToString("N0");
        FoldersText.Text = node.FolderCount.ToString("N0");
        UpdateNavigationButtons();
        ApplyFilter();
        RenderTreemap();
        RefreshChartIfVisible();
        if (TypesAgeScopeBox.SelectedIndex == 1)
        {
            PopulateTypesAndAge();
        }
    }

    private void AddNavigationEntry(ScanNode node)
    {
        if (_navigationIndex >= 0 &&
            _navigationIndex < _navigationHistory.Count &&
            _navigationHistory[_navigationIndex].FullPath.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_navigationIndex + 1 < _navigationHistory.Count)
        {
            _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
        }

        _navigationHistory.Add(node);
        _navigationIndex = _navigationHistory.Count - 1;
    }

    private void NavigateHistory(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _navigationHistory.Count || newIndex == _navigationIndex)
        {
            return;
        }

        _navigationIndex = newIndex;
        NavigateToNode(_navigationHistory[_navigationIndex], addToHistory: false);
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _navigationIndex > 0;
        ForwardButton.IsEnabled = _navigationIndex >= 0 && _navigationIndex + 1 < _navigationHistory.Count;
        UpButton.IsEnabled = _currentNode is not null && GetParentPath(_currentNode.FullPath) is not null;
    }

    // Only folders are ever looked up by path (tree, breadcrumb, Up); files are ~80% of nodes.
    private void IndexScanNodes(ScanNode node, ScanNode? parent)
    {
        var stack = new Stack<(ScanNode Node, ScanNode? Parent)>();
        stack.Push((node, parent));
        while (stack.TryPop(out var entry))
        {
            _nodesByPath[entry.Node.FullPath] = entry.Node;
            _parentsByPath[entry.Node.FullPath] = entry.Parent;
            foreach (var child in entry.Node.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push((child, entry.Node));
                }
            }
        }
    }

    private static IReadOnlyList<string> GetPathChain(string path) => ScanPaths.GetChain(path);

    private static string? GetParentPath(string path) => ScanPaths.GetParent(path);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private void SelectDriveForPath(string path)
    {
        var root = Path.GetPathRoot(path);
        var matchingDrive = (DrivePicker.ItemsSource as IEnumerable<DriveRow>)?
            .FirstOrDefault(row => PathsEqual(row.Drive.Name, root));
        if (matchingDrive is null || ReferenceEquals(DrivePicker.SelectedItem, matchingDrive))
        {
            return;
        }

        _syncingDrivePicker = true;
        try
        {
            DrivePicker.SelectedItem = matchingDrive;
        }
        finally
        {
            _syncingDrivePicker = false;
        }
    }

    private void UpdateBreadcrumb(ScanNode node)
    {
        PathBreadcrumb.ItemsSource = GetPathChain(node.FullPath)
            .Select(path =>
            {
                _nodesByPath.TryGetValue(path, out var scannedNode);
                return new BreadcrumbSegment(new FolderTreeRow(path, scannedNode).Name, path, scannedNode);
            })
            .ToArray();
        ShowBreadcrumb();
    }

    private void SynchronizeTreeToNode(ScanNode node)
    {
        TreeViewNode? currentTreeNode = null;
        foreach (var path in GetPathChain(node.FullPath))
        {
            if (!_treeNodesByPath.TryGetValue(path, out var realizedNode))
            {
                if (currentTreeNode is null)
                {
                    return;
                }
                if (currentTreeNode.HasUnrealizedChildren)
                {
                    PopulateTreeChildren(currentTreeNode);
                }
                if (!_treeNodesByPath.TryGetValue(path, out realizedNode))
                {
                    return;
                }
            }

            currentTreeNode = realizedNode;
            if (!path.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                currentTreeNode.IsExpanded = true;
            }
        }

        if (currentTreeNode is null)
        {
            return;
        }

        if (currentTreeNode.HasUnrealizedChildren)
        {
            PopulateTreeChildren(currentTreeNode);
        }
        currentTreeNode.IsExpanded = currentTreeNode.Children.Count > 0;

        _syncingTreeSelection = true;
        try
        {
            DirectoryTree.SelectedNode = currentTreeNode;
        }
        finally
        {
            _syncingTreeSelection = false;
        }

        var expectedPath = node.FullPath;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentNode?.FullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }
            DirectoryTree.UpdateLayout();
            if (DirectoryTree.ContainerFromNode(currentTreeNode) is TreeViewItem container)
            {
                container.StartBringIntoView();
            }
        });
    }

    private void ShowBreadcrumb()
    {
        if (_currentNode is null)
        {
            return;
        }
        PathBox.Visibility = Visibility.Collapsed;
        PathBreadcrumb.Visibility = Visibility.Visible;
        EditPathButton.Visibility = Visibility.Visible;
    }

    private void ShowPathEditor()
    {
        PathBreadcrumb.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        EditPathButton.Visibility = Visibility.Collapsed;
        PathBox.Focus(FocusState.Programmatic);
        PathBox.SelectAll();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigateHistory(_navigationIndex - 1);

    private void Forward_Click(object sender, RoutedEventArgs e) => NavigateHistory(_navigationIndex + 1);

    private void EditPath_Click(object sender, RoutedEventArgs e) => ShowPathEditor();

    private async void PathBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbSegment segment)
        {
            if (segment.Source is not null)
            {
                ShowNode(segment.Source);
            }
            else
            {
                await StartScanAsync(segment.FullPath);
            }
        }
    }

    private void ApplyFilter()
    {
        if (!_loaded)
        {
            return;
        }

        if (_currentNode is null && _liveTopLevel is { } liveTopLevel)
        {
            ShowLiveResults(liveTopLevel);
            return;
        }

        if (_currentNode is null || IsDeletedByApp(_currentNode))
        {
            ChildrenList.ItemsSource = null;
            return;
        }

        var nodes = SortNodes(FilterByQuery(_currentNode.Children));

        // Hide items this session deleted; a per-row disk check froze the UI for seconds in large folders.
        var displayAllocated = _displayAllocated;
        var parentSize = GetMeasure(_currentNode, displayAllocated);
        ChildrenList.ItemsSource = nodes
            .Where(node => !_deletedPaths.Contains(node.FullPath))
            .Take(100_000)
            .Select(node => new NodeRow(node, displayAllocated, ResultColumnWidths, parentSize))
            .ToArray();
        ScheduleHeaderScrollbarSync();
    }

    private IEnumerable<ScanNode> FilterByQuery(IEnumerable<ScanNode> nodes)
    {
        var query = FilterBox.Text.Trim();
        return string.IsNullOrWhiteSpace(query)
            ? nodes
            : nodes.Where(node =>
                node.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                node.FullPath.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool IsDeletedByApp(ScanNode node)
    {
        ScanNode? current = node;
        while (current is not null && _deletedPaths.Count > 0)
        {
            if (_deletedPaths.Contains(current.FullPath))
            {
                return true;
            }
            _parentsByPath.TryGetValue(current.FullPath, out current);
        }
        return false;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        // Rebuilding the rows of a large folder on every keystroke makes typing lag.
        _filterDebounceTimer ??= CreateFilterDebounceTimer();
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateFilterDebounceTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ApplyFilter();
        return timer;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _syncingSortControls || SortBox.SelectedIndex < 0)
        {
            return;
        }

        _sortColumn = (ResultSortColumn)SortBox.SelectedIndex;
        _sortDescending = !IsTextSort(_sortColumn);
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
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ResultColumnKey>(tag, out var key))
        {
            return;
        }

        var selectedColumn = ResultColumns.Get(key).Sort;
        if (_sortColumn == selectedColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = selectedColumn;
            _sortDescending = !IsTextSort(selectedColumn);
        }

        _syncingSortControls = true;
        SortBox.SelectedIndex = (int)_sortColumn;
        _syncingSortControls = false;
        UpdateSortIndicators();
        ApplyFilter();
    }

    private static bool IsTextSort(ResultSortColumn column) =>
        column is ResultSortColumn.Name or ResultSortColumn.Type or ResultSortColumn.Attributes;

    private IEnumerable<ScanNode> SortNodes(IEnumerable<ScanNode> nodes)
    {
        var text = StringComparer.CurrentCultureIgnoreCase;
        return (_sortColumn, _sortDescending) switch
        {
            (ResultSortColumn.Name, false) => nodes.OrderBy(static node => node.Name, text),
            (ResultSortColumn.Name, true) => nodes.OrderByDescending(static node => node.Name, text),
            (ResultSortColumn.Size, false) => nodes.OrderBy(static node => node.Size),
            (ResultSortColumn.Size, true) => nodes.OrderByDescending(static node => node.Size),
            (ResultSortColumn.Allocated, false) => nodes.OrderBy(static node => node.AllocatedSize),
            (ResultSortColumn.Allocated, true) => nodes.OrderByDescending(static node => node.AllocatedSize),
            (ResultSortColumn.Modified, false) => nodes.OrderBy(static node => node.LastWriteUtc),
            (ResultSortColumn.Modified, true) => nodes.OrderByDescending(static node => node.LastWriteUtc),
            (ResultSortColumn.FileCount, false) => nodes.OrderBy(static node => node.FileCount),
            (ResultSortColumn.FileCount, true) => nodes.OrderByDescending(static node => node.FileCount),
            (ResultSortColumn.FolderCount, false) => nodes.OrderBy(static node => node.FolderCount),
            (ResultSortColumn.FolderCount, true) => nodes.OrderByDescending(static node => node.FolderCount),
            (ResultSortColumn.Created, false) => nodes.OrderBy(static node => node.CreatedUtc),
            (ResultSortColumn.Created, true) => nodes.OrderByDescending(static node => node.CreatedUtc),
            (ResultSortColumn.Accessed, false) => nodes.OrderBy(static node => node.LastAccessUtc),
            (ResultSortColumn.Accessed, true) => nodes.OrderByDescending(static node => node.LastAccessUtc),
            // Folders first, then by extension, like Explorer's Type column.
            (ResultSortColumn.Type, false) => nodes.OrderBy(static node => node.IsDirectory ? 0 : 1).ThenBy(static node => node.Extension, text).ThenBy(static node => node.Name, text),
            (ResultSortColumn.Type, true) => nodes.OrderByDescending(static node => node.IsDirectory ? 0 : 1).ThenByDescending(static node => node.Extension, text).ThenBy(static node => node.Name, text),
            (ResultSortColumn.Attributes, false) => nodes.OrderBy(static node => node.Attributes).ThenBy(static node => node.Name, text),
            _ => nodes.OrderByDescending(static node => node.Attributes).ThenBy(static node => node.Name, text)
        };
    }

    private void UpdateSortIndicators()
    {
        if (SortDirectionButton is null)
        {
            return;
        }

        foreach (var (key, header) in _resultHeaderTexts)
        {
            var column = ResultColumns.Get(key);
            // Size and % of parent share the size sort; only the Size header shows the arrow.
            var sorted = column.Sort == _sortColumn && !(key == ResultColumnKey.Percent && _resultHeaderTexts.ContainsKey(ResultColumnKey.Size));
            header.Text = sorted ? $"{column.Title} {(_sortDescending ? "↓" : "↑")}" : column.Title;
        }
        SortDirectionButton.Content = _sortDescending ? "↓" : "↑";
        ToolTipService.SetToolTip(SortDirectionButton, LocalizationService.Get(_sortDescending ? "SortDescendingTip" : "SortAscendingTip"));
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

        if (row.IsLive && row.Source.IsDirectory)
        {
            // The folder's contents are not in the tree yet; it opens once the scan completes.
            StatusText.Text = LocalizationService.Get("LiveFolderNotReady");
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

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNode is null)
        {
            return;
        }

        if (_parentsByPath.TryGetValue(_currentNode.FullPath, out var parent) && parent is not null)
        {
            ShowNode(parent);
            return;
        }

        var parentPath = GetParentPath(_currentNode.FullPath);
        if (parentPath is not null)
        {
            await StartScanAsync(parentPath);
        }
    }

    private void TreeSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var availableMaximum = Math.Max(TreePaneColumn.MinWidth, WorkspaceGrid.ActualWidth - 680);
        var maximum = Math.Min(TreePaneColumn.MaxWidth, availableMaximum);
        TreePaneColumn.Width = new GridLength(Math.Clamp(
            TreePaneColumn.ActualWidth + e.HorizontalChange,
            TreePaneColumn.MinWidth,
            maximum));
    }

    private void TreeSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        TreePaneColumn.Width = new GridLength(DefaultTreePaneWidth);
        SaveLayoutSettings();
        e.Handled = true;
    }

    private void TreeSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => SaveLayoutSettings();

    private void ResultColumnSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ResultColumnKey>(tag, out var key))
        {
            return;
        }

        var column = ResultColumns.Get(key);
        var width = Math.Clamp(GetResultColumnActualWidth(key) + e.HorizontalChange, column.MinWidth, GetResultColumnMaximum(column));
        ResultColumnWidths.Set(key, new GridLength(width));
    }

    private void ResultColumnSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ResultColumnKey>(tag, out var key))
        {
            FitResultColumn(key);
            e.Handled = true;
        }
    }

    private void FitResultColumn(ResultColumnKey key)
    {
        var column = ResultColumns.Get(key);
        var rows = ChildrenList.ItemsSource as IEnumerable<NodeRow> ?? [];
        var values = key == ResultColumnKey.Name
            ? rows.SelectMany(static row => new[] { row.Name, row.CountText })
            : rows.Select(column.Text);
        var header = _resultHeaderTexts.TryGetValue(key, out var headerText) ? headerText.Text : column.Title;

        var candidates = values
            .Append(header)
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.CurrentCulture)
            .OrderByDescending(static value => value.Length)
            .Take(128);
        var measurer = new TextBlock { FontSize = 14 };
        var measured = 0d;
        foreach (var value in candidates)
        {
            measurer.Text = value;
            measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            measured = Math.Max(measured, measurer.DesiredSize.Width);
        }

        // The share column also holds a bar next to its number.
        var padding = key switch
        {
            ResultColumnKey.Name => 24,
            ResultColumnKey.Percent => 90,
            _ => 30
        };
        ResultColumnWidths.Set(key, new GridLength(Math.Clamp(measured + padding, column.MinWidth, GetResultColumnMaximum(column))));
        SaveLayoutSettings();
    }

    private double GetResultColumnActualWidth(ResultColumnKey key) =>
        _resultHeaderColumns.TryGetValue(key, out var definition) ? definition.ActualWidth : 0;

    private double GetResultColumnMaximum(ResultColumnDefinition column)
    {
        var available = Math.Max(320, ResultHeaderHost.ActualWidth);
        return column.Key == ResultColumnKey.Name
            ? Math.Max(column.MinWidth, available * 0.60)
            : Math.Max(column.MinWidth, Math.Min(320, available * 0.42));
    }

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTreemap();

    private void TreemapCanvas_Loaded(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(RenderTreemap);

    private void TreemapCanvas_LayoutUpdated(object? sender, object e)
    {
        if (_loaded &&
            ReferenceEquals(WorkspaceTabs.SelectedItem, TreemapTab) &&
            TreemapCanvas.Children.Count == 0 &&
            TreemapCanvas.ActualWidth >= 20 &&
            TreemapCanvas.ActualHeight >= 20 &&
            _currentNode?.Children.Any(node => !IsDeletedPath(node.FullPath) && GetMeasure(node, _displayAllocated) > 0) == true)
        {
            RenderTreemap();
        }
    }

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && WorkspaceTabs.SelectedItem is { } selectedTab && !ReferenceEquals(selectedTab, IssuesTab))
        {
            _lastNonIssuesTab = selectedTab;
        }

        if (_loaded && ReferenceEquals(WorkspaceTabs.SelectedItem, TreemapTab))
        {
            await Task.Delay(50);
            RenderTreemap();
        }
        else if (_loaded && ReferenceEquals(WorkspaceTabs.SelectedItem, ChartsTab))
        {
            RefreshChartIfVisible();
        }
    }

    private void RenderTreemap()
    {
        TreemapCanvas.Children.Clear();
        var source = _currentNode?.Children ?? _liveTopLevel;
        if (source is null || TreemapCanvas.ActualWidth < 20 || TreemapCanvas.ActualHeight < 20)
        {
            return;
        }

        var displayAllocated = _displayAllocated;
        var nodes = source
            .Where(node => !IsDeletedPath(node.FullPath) && GetMeasure(node, displayAllocated) > 0)
            .OrderByDescending(node => GetMeasure(node, displayAllocated))
            .Take(250)
            .ToArray();
        var tiles = Squarify(nodes, TreemapCanvas.ActualWidth, TreemapCanvas.ActualHeight, displayAllocated);
        if (_currentNode is not null)
        {
            _treemapMenu ??= CreateTreemapTileMenu();
        }
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
            if (_currentNode is not null)
            {
                border.ContextFlyout = _treemapMenu;
                border.RightTapped += (_, _) => _treemapTargetNode = tile.Node;
            }
            Canvas.SetLeft(border, tile.X);
            Canvas.SetTop(border, tile.Y);
            TreemapCanvas.Children.Add(border);
        }
    }

    private void TreemapTile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: ScanNode node } && _currentNode is not null)
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
        if (_scanResult is not { } scan)
        {
            ShowNotification(LocalizationService.Get("ScanBeforeDuplicates"), InfoBarSeverity.Warning);
            return;
        }

        _workCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _workCancellation = cancellation;
        SetBusy(true, LocalizationService.Get("PreparingDuplicates"));
        var minimumBytes = (long)Math.Max(0, double.IsNaN(DuplicateMinimumBox.Value) ? 1 : DuplicateMinimumBox.Value) * 1024 * 1024;
        var progress = new Progress<DuplicateProgress>(value =>
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_workCancellation, cancellation) || !ReferenceEquals(_scanResult, scan))
            {
                return;
            }
            var phase = LocalizationService.Get(value.Phase == "Sampling" ? "DuplicatePhaseSampling" : "DuplicatePhaseVerifying");
            StatusText.Text = LocalizationService.Format("DuplicateProgress", phase, value.CurrentPath);
            StatusDetailText.Text = $"{value.Processed:N0} / {value.Total:N0}";
            WorkProgress.IsIndeterminate = value.Total == 0;
            WorkProgress.Value = value.Total == 0 ? 0 : value.Processed * 100d / value.Total;
        });

        try
        {
            // Snapshot candidate file references under the edit lock so deletions cannot mutate Children while FindAsync iterates.
            var snapshotFiles = await ReadScanAsync(
                () => scan.Root.Files().Where(file => file.Size >= minimumBytes && !IsDeletedPath(file.FullPath)).ToList(),
                cancellation.Token);
            var snapshotRoot = new ScanNode
            {
                Name = scan.Root.Name,
                FullPath = scan.Root.FullPath,
                IsDirectory = true,
                Children = snapshotFiles
            };
            var result = await _duplicateFinder.FindAsync(snapshotRoot, minimumBytes, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_workCancellation, cancellation) || !ReferenceEquals(_scanResult, scan))
            {
                return;
            }

            var filteredGroups = result.Groups
                .Select(group => group with { Files = group.Files.Where(file => !IsDeletedPath(file.Path)).ToArray() })
                .Where(static group => group.Files.Count > 1)
                .ToArray();
            var rows = filteredGroups
                .SelectMany((group, index) => group.Files.Select(file => new DuplicateRow(index + 1, group, file)))
                .ToArray();
            _duplicateSkippedCloudFiles = result.SkippedCloudFiles;
            ShowDuplicateRows(rows);
            StatusText.Text = LocalizationService.Get("DuplicateComplete");
            StatusDetailText.Text = LocalizationService.Format("VerifiedGroups", filteredGroups.Length);
            if (result.Issues.Count > 0)
            {
                ShowNotification(LocalizationService.Format("DuplicateHashIssues", result.Issues.Count), InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_workCancellation, cancellation))
            {
                StatusText.Text = LocalizationService.Get("DuplicateCanceled");
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_workCancellation, cancellation))
            {
                ShowNotification(exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_workCancellation, cancellation))
            {
                SetBusy(false);
                _workCancellation = null;
            }
            cancellation.Dispose();
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

    private async void InsightList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (InsightsList.SelectedItem is not InsightRow row)
        {
            return;
        }

        if (row.Insight.Kind == "TemporaryFiles" && _scanResult is not null)
        {
            ResetSearchInputs();
            SearchNameBox.Text = "*.tmp; *.temp; *.dmp";
            WorkspaceTabs.SelectedItem = SearchTab;
            await RunSearchAsync();
        }
        else if (row.Insight.Kind == "EmptyFiles" && _scanResult is not null)
        {
            ResetSearchInputs();
            SearchMinSizeBox.Value = 0;
            SearchMaxSizeBox.Value = 0;
            WorkspaceTabs.SelectedItem = SearchTab;
            await RunSearchAsync();
        }
        else
        {
            _fileOperations.ShowInExplorer(row.Path);
        }
    }

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        // A filtered view would record every non-matching file as removed.
        if ((_basePresentation?.Result ?? _scanResult) is not { } result)
        {
            return;
        }

        var path = NativeFileDialogs.PickSaveFile(
            App.WindowHandle,
            $"DiskLoom-{SanitizeFileName(result.Root.Name)}-{DateTime.Now:yyyyMMdd-HHmm}",
            LocalizationService.Get("FileSnapshot"),
            ".diskloom");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            SetBusy(true, LocalizationService.Get("SavingSnapshot"));
            await ReadScanAsync(async () =>
            {
                await _snapshotService.SaveAsync(result, path);
                return true;
            });
            StatusText.Text = LocalizationService.Format("SnapshotSaved", path);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndSideTask();
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if ((_basePresentation?.Result ?? _scanResult) is not { } result)
        {
            return;
        }

        var path = NativeFileDialogs.PickOpenFile(
            App.WindowHandle,
            LocalizationService.Get("FileSnapshot"),
            "*.diskloom");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            SetBusy(true, LocalizationService.Get("ComparingSnapshots"));
            var (older, changes) = await ReadScanAsync(async () =>
            {
                var loaded = await _snapshotService.LoadAsync(path);
                return (loaded, _snapshotService.Compare(loaded, _snapshotService.Create(result)));
            });
            _comparedSnapshot = older;
            ChangesList.ItemsSource = changes.Select(change => new ChangeRow(change, result.Root.FullPath)).ToArray();
            ChangesHeaderText.Text = LocalizationService.Format("ChangesSince", changes.Count, older.CreatedUtc.LocalDateTime, older.RootPath);
            WorkspaceTabs.SelectedItem = ChangesTab;
            StatusText.Text = LocalizationService.Get("ComparisonComplete");
            StatusDetailText.Text = LocalizationService.Format("ChangedPaths", changes.Count);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndSideTask();
        }
    }

    private void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        e.ItemContainer.ContextFlyout = e.InRecycleQueue ? null : FileActionsMenu;
    }

    private void FileActionsMenu_Opening(object sender, object e)
    {
        // The flyout opens on the row that was right-clicked, not on the ListView itself.
        var target = (sender as FlyoutBase)?.Target;
        _fileMenuList = FindVisualParent<ListView>(target);

        // Explorer-style: right-clicking a row outside the selection acts on that row alone,
        // so the menu can never delete a different item than the one under the pointer.
        if (_fileMenuList is not null &&
            FindVisualParent<ListViewItem>(target) is { } container &&
            _fileMenuList.ItemFromContainer(container) is { } item &&
            !_fileMenuList.SelectedItems.Contains(item))
        {
            _fileMenuList.SelectedItems.Clear();
            _fileMenuList.SelectedItems.Add(item);
        }

        var targets = GetMenuTargets();
        FileOpenMenuItem.IsEnabled = targets.Count == 1;
        FilePropertiesMenuItem.IsEnabled = targets.Count == 1;
        var canDelete = targets.Count > 0 && targets.All(CanDeleteTarget);
        FileRecycleMenuItem.IsEnabled = canDelete;
        FileDeleteMenuItem.IsEnabled = canDelete;
        FileCopyToMenuItem.IsEnabled = canDelete;
        FileMoveToMenuItem.IsEnabled = canDelete;
    }

    private IReadOnlyList<IFileActionRow> GetMenuTargets() => GetSelectedFileRows(_fileMenuList);

    // Selected rows in display order (SelectedItems is in click order).
    private static IReadOnlyList<IFileActionRow> GetSelectedFileRows(ListView? list)
    {
        if (list?.ItemsSource is not IEnumerable<IFileActionRow> rows)
        {
            return Array.Empty<IFileActionRow>();
        }

        var selected = list.SelectedItems.ToHashSet();
        return rows.Where(selected.Contains).ToArray();
    }

    private static bool CanDeleteTarget(IFileActionRow row)
    {
        var root = Path.GetPathRoot(row.Path);
        return row.CanDelete && !PathsEqual(row.Path, root) && !WindowsPaths.IsVolumeRoot(row.Path);
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuTargets() is [var row] && !RefuseTrimmedForShell(row.Path))
        {
            try
            {
                _fileOperations.Open(row.Path);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                ShowNotification(exception.Message, InfoBarSeverity.Error);
            }
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        // Explorer reveals one item per call, so use the first selected row.
        if (GetMenuTargets() is [var row, ..])
        {
            _fileOperations.ShowInExplorer(row.Path);
        }
    }

    private void CopySelectedPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = GetMenuTargets().Select(static row => row.Path).ToArray();
        if (paths.Length > 0)
        {
            CopyText(string.Join(Environment.NewLine, paths));
        }
    }

    private void ShowProperties_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuTargets() is [var row] && !RefuseTrimmedForShell(row.Path) && !_fileOperations.ShowProperties(App.WindowHandle, row.Path))
        {
            ShowNotification(LocalizationService.Format("PropertiesFailed", row.Path), InfoBarSeverity.Error);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _fileMenuList?.SelectAll();

    // Removes rows this session deleted (or that lived inside a deleted folder) from every file list.
    private void RefreshListsAfterDeletion()
    {
        ApplyFilter();
        LargestFilesList.ItemsSource = WithoutDeleted(LargestFilesList.ItemsSource as IEnumerable<NodeRow>);
        InsightsList.ItemsSource = WithoutDeleted(InsightsList.ItemsSource as IEnumerable<InsightRow>);
        if (WithoutDeleted(DuplicatesList.ItemsSource as IEnumerable<DuplicateRow>) is { } duplicates)
        {
            ShowDuplicateRows(duplicates.GroupBy(static row => row.GroupNumber).Where(static group => group.Count() > 1).SelectMany(static group => group).ToArray());
        }
        SearchResultsList.ItemsSource = WithoutDeleted(SearchResultsList.ItemsSource as IEnumerable<NodeRow>);
    }

    private T[]? WithoutDeleted<T>(IEnumerable<T>? rows) where T : IFileActionRow =>
        rows?.Where(row => !IsDeletedPath(row.Path)).ToArray();

    private bool IsDeletedPath(string path)
    {
        foreach (var deleted in _deletedPaths)
        {
            if (path.StartsWith(deleted, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == deleted.Length || path[deleted.Length] is '\\' or '/'))
            {
                return true;
            }
        }
        return false;
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => CopyText(_currentNode?.FullPath ?? PathBox.Text);

    private void ShowIssueInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (IssuesList.SelectedItem is not IssueRow row)
        {
            return;
        }

        var path = row.Path;
        if (!File.Exists(WindowsPaths.ToExtended(path)) && !Directory.Exists(WindowsPaths.ToExtended(path)))
        {
            var parent = ScanPaths.GetParent(path);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(WindowsPaths.ToExtended(parent)))
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

        if (!ReferenceEquals(WorkspaceTabs.SelectedItem, IssuesTab))
        {
            _lastNonIssuesTab = WorkspaceTabs.SelectedItem;
        }
        WorkspaceTabs.SelectedItem = IssuesTab;
        NotificationBar.IsOpen = false;
        IssuesList.Focus(FocusState.Programmatic);
    }

    private void IssuesBackButton_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceTabs.SelectedItem = _lastNonIssuesTab is TabViewItem tab && !ReferenceEquals(tab, IssuesTab) ? tab : OverviewTab;
    }

    private async void RecycleSelected_Click(object sender, RoutedEventArgs e) =>
        await DeleteTargetsAsync(GetMenuTargets(), permanently: false);

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) =>
        await DeleteTargetsAsync(GetMenuTargets(), permanently: true);

    private async void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryGetDeleteShortcut(e, out var permanently))
        {
            await DeleteTargetsAsync(GetSelectedFileRows((ListView)sender), permanently);
            return;
        }

        if (e.Key == VirtualKey.Enter && !e.KeyStatus.WasKeyDown)
        {
            var list = (ListView)sender;
            if (ReferenceEquals(list, ChildrenList))
            {
                e.Handled = true;
                ChildrenList_DoubleTapped(sender, new DoubleTappedRoutedEventArgs());
            }
            else if (ReferenceEquals(list, SearchResultsList))
            {
                e.Handled = true;
                SearchResultsList_DoubleTapped(sender, new DoubleTappedRoutedEventArgs());
            }
            else if (ReferenceEquals(list, LargestFilesList))
            {
                e.Handled = true;
                AnyFileList_DoubleTapped(sender, new DoubleTappedRoutedEventArgs());
            }
            else if (ReferenceEquals(list, DuplicatesList))
            {
                e.Handled = true;
                ShowDuplicateInExplorer_Click(sender, new RoutedEventArgs());
            }
            else if (ReferenceEquals(list, InsightsList))
            {
                e.Handled = true;
                InsightList_DoubleTapped(sender, new DoubleTappedRoutedEventArgs());
            }
        }
    }

    private async void DirectoryTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryGetDeleteShortcut(e, out var permanently))
        {
            await DeleteTreeTargetAsync(DirectoryTree.SelectedNode?.Content as FolderTreeRow, permanently);
        }
    }

    private static bool TryGetDeleteShortcut(KeyRoutedEventArgs e, out bool permanently)
    {
        permanently = false;
        if (e.Key != VirtualKey.Delete || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu) ||
            IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            return false;
        }

        e.Handled = true;
        permanently = IsKeyDown(VirtualKey.Shift);
        return !e.KeyStatus.WasKeyDown;

        static bool IsKeyDown(VirtualKey key) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    }

    private async Task DeleteTargetsAsync(IReadOnlyList<IFileActionRow> rows, bool permanently, bool fromTree = false)
    {
        if (_removalInProgress || _workCancellation is not null || rows.Count == 0 || !rows.All(CanDeleteTarget))
        {
            return;
        }

        // Permanent deletion addresses names ending in a dot or space exactly; the Recycle Bin cannot.
        if (!permanently && RefuseTrimmedNames(rows.Select(static row => row.Path)))
        {
            return;
        }

        _removalInProgress = true;
        try
        {
            await ConfirmAndDeleteAsync(rows, permanently, fromTree);
        }
        finally
        {
            _removalInProgress = false;
        }
    }

    private async Task ConfirmAndDeleteAsync(IReadOnlyList<IFileActionRow> rows, bool permanently, bool fromTree)
    {
        // A duplicate check is a snapshot: refuse to delete a copy whose content may have changed since, and keep
        // at least one intact copy of each file, also after "Select all" or when another program removed the rest.
        var duplicates = rows.OfType<DuplicateRow>().ToArray();
        bool DuplicatesStillSafe()
        {
            var changed = duplicates.Count(static row => !DuplicateFinder.IsUnchanged(row.File));
            if (changed > 0)
            {
                ShowNotification(LocalizationService.Format("DuplicateChangedSinceCheck", changed), InfoBarSeverity.Warning);
                return false;
            }

            if (duplicates.Length > 0)
            {
                var listed = (DuplicatesList.ItemsSource as IEnumerable<DuplicateRow> ?? []).ToArray();
                var losing = DuplicateFinder.GroupsLosingEveryCopy(
                    duplicates.Select(static row => row.Group).Distinct(),
                    listed.Select(static row => row.Path),
                    duplicates.Select(static row => row.Path));
                if (losing.Count > 0)
                {
                    ShowNotification(LocalizationService.Format("DuplicateKeepOneCopy", losing.Count), InfoBarSeverity.Warning);
                    return false;
                }
            }

            return true;
        }

        if (!DuplicatesStillSafe())
        {
            return;
        }

        var paths = rows.Select(static row => row.Path).ToArray();
        var subject = string.Join(Environment.NewLine, paths) + FilteredDeletionNote(paths);
        if (rows.Count > 1)
        {
            subject = LocalizationService.Format("MultipleItemsSummary", rows.Count) + Environment.NewLine + Environment.NewLine + subject;
        }
        var title = permanently
            ? (fromTree ? "DeleteFolderTitle" : rows.Count == 1 ? "DeleteTitle" : "DeleteItemsTitle")
            : (fromTree ? "RecycleFolderTitle" : "RecycleTitle");
        var content = new ScrollViewer
        {
            MaxHeight = 240,
            Content = new TextBlock
            {
                Text = permanently ? LocalizationService.Format("DeleteWarning", subject) : subject,
                TextWrapping = TextWrapping.Wrap
            }
        };
        var dialog = CreateDialog(
            LocalizationService.Get(title),
            content,
            LocalizationService.Get(permanently ? "DeletePermanently" : "Recycle"),
            LocalizationService.Get("Cancel"),
            ContentDialogButton.Close);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        // The dialog may have been open for a while, so check again what it confirmed.
        if (!DuplicatesStillSafe())
        {
            return;
        }

        if (permanently && !Environment.IsPrivilegedProcess && duplicates.Length == 0 &&
            paths.All(AdministratorDeletion.CanRetryPath))
        {
            using var permissionCancellation = new CancellationTokenSource();
            var previousCancellation = _workCancellation;
            _workCancellation = permissionCancellation;
            SetBusy(true, LocalizationService.Get("CheckingPermissions"));
            bool needsElevation;
            try
            {
                needsElevation = await Task.Run(
                    () => paths.Any(path => _fileOperations.PermanentDeletionRequiresElevation(path, permissionCancellation.Token)),
                    permissionCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_workCancellation, permissionCancellation))
                {
                    _workCancellation = previousCancellation;
                    EndSideTask();
                }
            }

            if (needsElevation)
            {
                await DeleteWithAdministratorAsync(paths, fromTree);
                return;
            }
        }

        var removed = new List<string>();
        var failed = new List<(string Path, Exception Error)>();
        foreach (var row in rows)
        {
            try
            {
                if (permanently)
                {
                    await _fileOperations.DeletePermanentlyAsync(row.Path);
                }
                else
                {
                    await _fileOperations.RecycleAsync(row.Path, App.WindowHandle);
                }
                removed.Add(row.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                failed.Add((row.Path, exception));
            }
        }

        await ApplyRemovedPathsAsync(removed);
        var partly = await ApplyPartialRemovalAsync(failed.Select(static item => item.Path).ToArray());
        if (failed.Count > 0)
        {
            ShowNotification(
                LocalizationService.Format(permanently ? "DeleteFailedSummary" : "RecycleFailedSummary", failed.Count, rows.Count) +
                " " + failed[0].Error.Message +
                (partly ? " " + LocalizationService.Get("RemovalPartlyDone") : string.Empty),
                permanently ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
        }
        else
        {
            ShowNotification(
                rows.Count == 1
                    ? LocalizationService.Get(permanently ? (fromTree ? "FolderDeleted" : "Deleted") : (fromTree ? "FolderRecycled" : "Recycled"))
                    : LocalizationService.Format(permanently ? "DeletedMultiple" : "RecycledMultiple", rows.Count),
                permanently ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
    }

    private async Task DeleteWithAdministratorAsync(string[] paths, bool fromTree)
    {
        int exitCode;
        try
        {
            exitCode = await AdministratorDeletion.RequestAsync(paths, App.CurrentLanguage);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            ShowNotification(LocalizationService.Get("DeleteAsAdministratorCanceled"), InfoBarSeverity.Informational);
            return;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
            return;
        }

        var removed = paths.Where(IsDeletionConfirmedOnDisk).ToArray();
        var remaining = paths.Except(removed, StringComparer.OrdinalIgnoreCase).ToArray();
        await ApplyRemovedPathsAsync(removed);
        var partly = await ApplyPartialRemovalAsync(remaining);
        if (remaining.Length == 0)
        {
            ShowNotification(paths.Length == 1
                ? LocalizationService.Get(fromTree ? "FolderDeleted" : "Deleted")
                : LocalizationService.Format("DeletedMultiple", paths.Length), InfoBarSeverity.Warning);
            return;
        }

        var message = LocalizationService.Get(exitCode == AdministratorDeletion.Canceled
            ? "AdministratorDeletionCanceled"
            : "DeleteAsAdministratorIncomplete");
        ShowNotification(message + (partly ? " " + LocalizationService.Get("RemovalPartlyDone") : string.Empty), InfoBarSeverity.Warning);
    }

    private static bool IsDeletionConfirmedOnDisk(string path)
    {
        try
        {
            _ = File.GetAttributes(WindowsPaths.ToExtended(path));
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(interactive: true);

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        // Once the user chose to install, failures must be shown even for the silent startup check.
        var installRequested = false;
        try
        {
            var configurationPath = Path.Combine(AppContext.BaseDirectory, "update-config.json");
            var configuration = await UpdateService.LoadConfigurationAsync(configurationPath);
            if (!interactive && (!configuration.CheckOnStartup || !ShouldRunScheduledUpdateCheck(configuration.CheckIntervalHours)))
            {
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
            // Recorded before the request, so an offline or rate-limited check is not repeated at every start.
            RecordUpdateCheck();
            var result = await _updateService.CheckAsync(configuration, currentVersion, preferredLanguage: App.CurrentLanguage);
            if (!result.IsConfigured)
            {
                if (interactive)
                {
                    await ShowDialogAsync(CreateDialog(
                        LocalizationService.Get("UpdatesTitle"),
                        LocalizationService.Format("UpdateNotConfigured", configurationPath),
                        LocalizationService.Get("Ok")));
                }
                return;
            }

            if (!result.IsUpdateAvailable || result.Manifest is null)
            {
                if (interactive)
                {
                    await ShowDialogAsync(CreateDialog(LocalizationService.Get("UpdatesTitle"), LocalizationService.Get("UpdateCurrent"), LocalizationService.Get("Ok")));
                }
                return;
            }

            if (!interactive)
            {
                await WaitForNoDialogAsync();
            }

            var availableVersion = result.Manifest.Version;
            var canInstallDirectly = UpdateService.CanInstallDirectly(result.Manifest, configuration);
            var prompt = CreateDialog(
                LocalizationService.Format("UpdateAvailable", availableVersion),
                string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotes)
                    ? LocalizationService.Get(canInstallDirectly ? "UpdatePrompt" : "OpenGitHubPrompt")
                    : result.Manifest.ReleaseNotes,
                LocalizationService.Get(canInstallDirectly ? "DownloadInstall" : "OpenGitHubRelease"),
                LocalizationService.Get("Later"),
                result.Manifest.Mandatory ? ContentDialogButton.Primary : ContentDialogButton.Close);
            if (await ShowDialogAsync(prompt) != ContentDialogResult.Primary)
            {
                return;
            }

            if (!canInstallDirectly)
            {
                var releaseUrl = Uri.TryCreate(result.Manifest.ReleaseNotesUrl, UriKind.Absolute, out var notes) &&
                    notes.Scheme == Uri.UriSchemeHttps && notes.UserInfo.Length == 0
                    ? notes.AbsoluteUri
                    : "https://github.com/Naxterra/DiskLoom/releases/latest";
                using Process? browser = Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                return;
            }

            installRequested = true;
            SetBusy(true, LocalizationService.Get("DownloadingUpdate"), indeterminate: false);
            var progress = new Progress<double>(value => WorkProgress.Value = value * 100);
            var installer = await _updateService.DownloadAndVerifyAsync(result.Manifest, configuration, progress);
            StatusText.Text = LocalizationService.Get("StartingInstaller");
            using var installerProcess = _updateService.StartInstaller(installer, relaunchPath: Environment.ProcessPath,
                verifiedSha256: result.Manifest.Sha256);
            App.Window.Close();
        }
        catch (Exception exception)
        {
            if (interactive || installRequested)
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
        await ShowDialogAsync(CreateDialog(
            $"DiskLoom {version}",
            LocalizationService.Format("AboutContent", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, CreatorIdentity.AboutLine),
            LocalizationService.Get("Close")));
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
        var excludeSystemBox = new CheckBox
        {
            Content = LocalizationService.Get("SettingsExcludeSystem"),
            IsChecked = App.Settings.ExcludeSystemFolders
        };
        var allocatedBox = new CheckBox
        {
            Content = LocalizationService.Get("SettingsDisplayAllocated"),
            IsChecked = _displayAllocated
        };
        var content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 340,
            Children =
            {
                languageBox,
                excludeSystemBox,
                allocatedBox,
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
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            var language = languageBox.SelectedIndex == 1 ? "de-DE" : "en-US";
            var excludeSystem = excludeSystemBox.IsChecked == true;
            var displayAllocated = allocatedBox.IsChecked == true;
            var excludeChanged = excludeSystem != App.Settings.ExcludeSystemFolders;

            App.Settings = App.Settings with
            {
                ExcludeSystemFolders = excludeSystem,
                DisplayAllocatedSize = displayAllocated
            };
            AppSettings.Save(App.Settings);

            if (language != App.CurrentLanguage)
            {
                App.RestartWithLanguage(language, _scanResult?.Root.FullPath);
                return;
            }

            if (displayAllocated != _displayAllocated)
            {
                ApplyDisplayAllocatedSetting(displayAllocated);
            }

            if (excludeChanged)
            {
                _scanCache.Clear();
                _scanCacheOrder.Clear();
                if (_scanResult is { } currentScan)
                {
                    await StartScanAsync(currentScan.Root.FullPath);
                }
            }
        }
    }

    private void ResetResults()
    {
        _treeNodesBeingPopulated.Clear();
        RebuildDirectoryTree(result: null);
        ChildrenList.ItemsSource = null;
        LargestFilesList.ItemsSource = null;
        ExtensionsList.ItemsSource = null;
        AgeList.ItemsSource = null;
        DuplicatesList.ItemsSource = null;
        DuplicateSelectButton.IsEnabled = false;
        ShowDuplicateButton.IsEnabled = false;
        InsightsList.ItemsSource = null;
        ChangesList.ItemsSource = null;
        _comparedSnapshot = null;
        IssuesList.ItemsSource = null;
        SearchResultsList.ItemsSource = null;
        SearchSummaryText.Text = LocalizationService.Get("SearchSummaryDefault");
        ClearLiveResults();
        ClearChart();
        _basePresentation = null;
        ResetTreeFilterState();
        IssuesTab.Header = LocalizationService.Get("IssuesTabLabel");
        TreemapCanvas.Children.Clear();
        LogicalSizeText.Text = "—";
        AllocatedSizeText.Text = "—";
        FilesText.Text = "—";
        FoldersText.Text = "—";
        TreeCountText.Text = LocalizationService.Get("Scanning");
        _scanResult = null;
        _currentNode = null;
        _nodesByPath.Clear();
        _parentsByPath.Clear();
        _deletedPaths.Clear();
        _navigationHistory.Clear();
        _navigationIndex = -1;
        RefreshButton.IsEnabled = false;
        FilterButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        SaveSnapshotButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        UpButton.IsEnabled = false;
        PathBreadcrumb.ItemsSource = null;
        PathBreadcrumb.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        EditPathButton.Visibility = Visibility.Collapsed;
        NotificationActionButton.Visibility = Visibility.Collapsed;
        NotificationBar.IsOpen = false;
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

        // Confirmations close by themselves; warnings and errors stay until they are dismissed.
        _notificationTimer ??= CreateNotificationTimer();
        _notificationTimer.Stop();
        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational && !showIssuesAction)
        {
            _notificationTimer.Start();
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notificationTimer;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateNotificationTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(8);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => NotificationBar.IsOpen = false;
        return timer;
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

    private static bool IsAccessDeniedIssue(ScanIssue issue) =>
        issue.ErrorType is nameof(UnauthorizedAccessException) or "SecurityException";

    private ContentDialog CreateDialog(
        string title,
        object content,
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
            AtomicFile.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
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

    private sealed record ScanPresentation(ScanResult Result, IReadOnlyList<ScanNode> LargestFiles, IReadOnlyList<StorageInsight> Insights);
    private sealed record AreaItem(ScanNode Node, double Area);
    private readonly record struct LayoutRect(double X, double Y, double Width, double Height);
    private sealed record TreemapTile(ScanNode Node, double X, double Y, double Width, double Height);
}
