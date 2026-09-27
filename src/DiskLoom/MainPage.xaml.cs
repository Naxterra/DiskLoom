using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DiskLoom.Core.Models;
using DiskLoom.Core.Services;
using DiskLoom.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private const double DefaultTreePaneWidth = 420;
    private const int ArrowCursorId = 32512;
    private const int ResizeHorizontalCursorId = 32644;
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
    private object? _lastNonIssuesTab;
    private int _navigationIndex = -1;
    private long _scanRequestId;

    public ResultColumnLayout ResultColumnWidths { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        foreach (var list in new[] { ChildrenList, LargestFilesList, InsightsList, DuplicatesList, SearchResultsList })
        {
            list.ContextFlyout = FileActionsMenu;
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
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
        _drives = _driveService.GetDrives();
        DrivePicker.ItemsSource = _drives.Select(static drive => new DriveRow(drive)).ToArray();
        RebuildDirectoryTree(result: null);
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
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
            return;
        }

        if (!Directory.Exists(path))
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
                FollowReparsePoints = false
            }, progress, cancellation.Token);
            var presentation = await Task.Run(() => CreatePresentation(result), cancellation.Token);

            if (cancellation.IsCancellationRequested || requestId != Volatile.Read(ref _scanRequestId))
            {
                return;
            }

            _scanCache[path] = presentation;
            ActivateScanResult(presentation, loadedFromCache: false);
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

    private ScanPresentation CreatePresentation(ScanResult result) => new(
        result,
        result.Root.Files()
            .OrderByDescending(static file => GetMeasure(file, DisplayAllocatedMeasurements))
            .Take(1000)
            .ToArray(),
        _insightService.Analyze(result));

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
        ShowNode(preferredPath is not null && _nodesByPath.TryGetValue(preferredPath, out var preferred) ? preferred : result.Root);
    }

    private void PopulateScan(ScanPresentation presentation)
    {
        var result = presentation.Result;
        RebuildDirectoryTree(result);
        TreeCountText.Text = $"{result.Root.FolderCount + 1:N0}";

        LargestFilesList.ItemsSource = presentation.LargestFiles
            .Select(static file => new NodeRow(file, DisplayAllocatedMeasurements))
            .ToArray();
        ExtensionsList.ItemsSource = result.Extensions.Take(500).Select(static item => new ExtensionRow(item)).ToArray();
        AgeList.ItemsSource = result.Ages.Select(static item => new AgeRow(item)).ToArray();
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
            var parentSize = (parentTreeNode?.Content as FolderTreeRow)?.Source?.Size ?? 0;
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
            Content = drive is null ? new FolderTreeRow(fullPath, node, parentSize) : new FolderTreeRow(drive, node),
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
                foreach (var child in source.Children.Where(static child => child.IsDirectory))
                {
                    treeNode.Children.Add(CreateTreeNode(child.FullPath, child, parentSize: source.Size));
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

    private void DirectoryTreeMenu_Opening(object sender, object e)
    {
        var row = GetTreeContextRow();
        var canDelete = row is not null && CanDeleteTreeFolder(row);
        TreeRecycleMenuItem.IsEnabled = canDelete;
        TreeDeleteMenuItem.IsEnabled = canDelete;
        TreeCopyToMenuItem.IsEnabled = row?.Source is not null && Directory.Exists(row.FullPath);
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
        if (row.Source?.IsDirectory != true || !Directory.Exists(row.FullPath))
        {
            return false;
        }
        var root = Path.GetPathRoot(row.FullPath);
        return string.IsNullOrWhiteSpace(root) ||
            !row.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
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

    private async void TreeRecycle_Click(object sender, RoutedEventArgs e)
    {
        var row = GetTreeContextRow();
        if (row is null || !CanDeleteTreeFolder(row))
        {
            return;
        }

        var dialog = CreateDialog(
            LocalizationService.Get("RecycleFolderTitle"),
            row.FullPath,
            LocalizationService.Get("Recycle"),
            LocalizationService.Get("Cancel"),
            ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _fileOperations.RecycleAsync(row.FullPath);
            _scanCache.Clear();
            await RefreshAfterTreeDeletionAsync();
            ShowNotification(LocalizationService.Get("FolderRecycled"), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void TreeDeletePermanently_Click(object sender, RoutedEventArgs e)
    {
        var row = GetTreeContextRow();
        if (row is null || !CanDeleteTreeFolder(row))
        {
            return;
        }

        var dialog = CreateDialog(
            LocalizationService.Get("DeleteFolderTitle"),
            LocalizationService.Format("DeleteWarning", row.FullPath),
            LocalizationService.Get("DeletePermanently"),
            LocalizationService.Get("Cancel"),
            ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _fileOperations.DeletePermanentlyAsync(row.FullPath);
            _scanCache.Clear();
            await RefreshAfterTreeDeletionAsync();
            ShowNotification(LocalizationService.Get("FolderDeleted"), InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowNotification(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshAfterTreeDeletionAsync()
    {
        var scanRoot = _scanResult?.Root.FullPath;
        if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(scanRoot))
        {
            await StartScanAsync(scanRoot);
            return;
        }

        var parent = string.IsNullOrWhiteSpace(scanRoot) ? null : GetParentPath(scanRoot);
        if (parent is not null && Directory.Exists(parent))
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

    private static IReadOnlyList<string> GetPathChain(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return [fullPath];
        }

        var chain = new List<string> { root };
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            return chain;
        }

        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            chain.Add(current);
        }
        return chain;
    }

    private static string? GetParentPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Directory.GetParent(fullPath)?.FullName;
    }

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
        const bool displayAllocated = DisplayAllocatedMeasurements;
        var parentSize = _currentNode.Size;
        ChildrenList.ItemsSource = nodes
            .Where(node => !_deletedPaths.Contains(node.FullPath))
            .Take(100_000)
            .Select(node => new NodeRow(node, displayAllocated, ResultColumnWidths, parentSize))
            .ToArray();
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
        e.Handled = true;
    }

    private void TreeSplitter_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetSystemCursor(ResizeHorizontalCursorId);

    private void TreeSplitter_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetSystemCursor(ArrowCursorId);

    private static void SetSystemCursor(int cursorId)
    {
        var cursor = LoadCursor(nint.Zero, (nint)cursorId);
        if (cursor != nint.Zero)
        {
            SetCursor(cursor);
        }
    }

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
            _currentNode?.Children.Any(node => GetMeasure(node, DisplayAllocatedMeasurements) > 0) == true)
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

        const bool displayAllocated = DisplayAllocatedMeasurements;
        var nodes = source
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
        if (_scanResult is null)
        {
            ShowNotification(LocalizationService.Get("ScanBeforeDuplicates"), InfoBarSeverity.Warning);
            return;
        }

        // Only cancel the previous operation's token here; disposing it now could race
        // with that operation still observing the token before it unwinds. Each flow
        // disposes its own CancellationTokenSource in its own finally block instead.
        _workCancellation?.Cancel();
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

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        // A filtered view would record every non-matching file as removed.
        if ((_basePresentation?.Result ?? _scanResult) is not { } result)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"DiskLoom-{SanitizeFileName(result.Root.Name)}-{DateTime.Now:yyyyMMdd-HHmm}"
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
            var path = file.Path;
            await Task.Run(() => _snapshotService.SaveAsync(result, path));
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
        if ((_basePresentation?.Result ?? _scanResult) is not { } result)
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
            var path = file.Path;
            var (older, changes) = await Task.Run(async () =>
            {
                var loaded = await _snapshotService.LoadAsync(path);
                return (loaded, _snapshotService.Compare(loaded, _snapshotService.Create(result)));
            });
            ChangesList.ItemsSource = changes.Select(static change => new ChangeRow(change)).ToArray();
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
            SetBusy(false);
        }
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

    // Selected rows in display order (SelectedItems is in click order).
    private IReadOnlyList<IFileActionRow> GetMenuTargets()
    {
        if (_fileMenuList?.ItemsSource is not IEnumerable<IFileActionRow> rows)
        {
            return Array.Empty<IFileActionRow>();
        }

        var selected = _fileMenuList.SelectedItems.ToHashSet();
        return rows.Where(selected.Contains).ToArray();
    }

    private static bool CanDeleteTarget(IFileActionRow row)
    {
        var root = Path.GetPathRoot(row.Path);
        return row.CanDelete && !PathsEqual(row.Path, root);
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuTargets() is [var row])
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
        if (GetMenuTargets() is [var row] && !_fileOperations.ShowProperties(App.WindowHandle, row.Path))
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
        DuplicatesList.ItemsSource = WithoutDeleted(DuplicatesList.ItemsSource as IEnumerable<DuplicateRow>);
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

    private async void RecycleSelected_Click(object sender, RoutedEventArgs e) => await DeleteMenuTargetsAsync(permanently: false);

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) => await DeleteMenuTargetsAsync(permanently: true);

    private async Task DeleteMenuTargetsAsync(bool permanently)
    {
        var rows = GetMenuTargets();
        if (rows.Count == 0 || !rows.All(CanDeleteTarget))
        {
            return;
        }

        var subject = rows.Count == 1 ? rows[0].Path : LocalizationService.Format("MultipleItemsSummary", rows.Count);
        var dialog = permanently
            ? CreateDialog(
                LocalizationService.Get("DeleteTitle"),
                LocalizationService.Format("DeleteWarning", subject),
                LocalizationService.Get("DeletePermanently"),
                LocalizationService.Get("Cancel"),
                ContentDialogButton.Close)
            : CreateDialog(
                LocalizationService.Get("RecycleTitle"),
                subject,
                LocalizationService.Get("Recycle"),
                LocalizationService.Get("Cancel"),
                ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var failures = 0;
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
                    await _fileOperations.RecycleAsync(row.Path);
                }
                _deletedPaths.Add(row.Path);
            }
            catch
            {
                failures++;
            }
        }

        _scanCache.Clear();
        RefreshListsAfterDeletion();
        if (failures > 0)
        {
            ShowNotification(
                LocalizationService.Format(permanently ? "DeleteFailedSummary" : "RecycleFailedSummary", failures, rows.Count),
                permanently ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
        }
        else
        {
            ShowNotification(
                rows.Count == 1
                    ? LocalizationService.Get(permanently ? "Deleted" : "Recycled")
                    : LocalizationService.Format(permanently ? "DeletedMultiple" : "RecycledMultiple", rows.Count),
                permanently ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
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
            var result = await _updateService.CheckAsync(configuration, currentVersion, preferredLanguage: App.CurrentLanguage);
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
            var canInstallDirectly = UpdateService.CanInstallDirectly(result.Manifest, configuration);
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

            installRequested = true;
            SetBusy(true, LocalizationService.Get("DownloadingUpdate"), indeterminate: false);
            var progress = new Progress<double>(value => WorkProgress.Value = value * 100);
            var installer = await _updateService.DownloadAndVerifyAsync(result.Manifest, configuration, progress);
            StatusText.Text = LocalizationService.Get("StartingInstaller");
            _updateService.StartInstaller(installer, relaunchPath: Environment.ProcessPath);
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
        _treeNodesBeingPopulated.Clear();
        RebuildDirectoryTree(result: null);
        ChildrenList.ItemsSource = null;
        LargestFilesList.ItemsSource = null;
        ExtensionsList.ItemsSource = null;
        AgeList.ItemsSource = null;
        DuplicatesList.ItemsSource = null;
        InsightsList.ItemsSource = null;
        ChangesList.ItemsSource = null;
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

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

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

    private sealed record ScanPresentation(ScanResult Result, IReadOnlyList<ScanNode> LargestFiles, IReadOnlyList<StorageInsight> Insights);
    private sealed record AreaItem(ScanNode Node, double Area);
    private readonly record struct LayoutRect(double X, double Y, double Width, double Height);
    private sealed record TreemapTile(ScanNode Node, double X, double Y, double Width, double Height);
}
