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
    private readonly Dictionary<string, ScanResult> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewNode> _treeNodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<TreeViewNode> _treeNodesBeingPopulated = [];
    private readonly List<ScanNode> _navigationHistory = [];
    private IReadOnlyList<DriveSummary> _drives = [];
    private CancellationTokenSource? _workCancellation;
    private ScanResult? _scanResult;
    private ScanNode? _currentNode;
    private FolderTreeRow? _treeContextRow;
    private bool _loaded;
    private SortColumn _sortColumn = SortColumn.Size;
    private bool _sortDescending = true;
    private bool _syncingSortControls;
    private bool _syncingDrivePicker;
    private bool _syncingTreeSelection;
    private bool _updatingTree;
    private bool _notificationShowsIssues;
    private int _lastNonIssuesTab;
    private int _navigationIndex = -1;
    private long _scanRequestId;

    public ResultColumnLayout ResultColumns { get; } = new();

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
        ToolTipService.SetToolTip(BackButton, LocalizationService.Get("Back"));
        ToolTipService.SetToolTip(ForwardButton, LocalizationService.Get("Forward"));
        ToolTipService.SetToolTip(UpButton, LocalizationService.Get("UpOneLevel"));
        ToolTipService.SetToolTip(EditPathButton, LocalizationService.Get("EditPath"));
        ToolTipService.SetToolTip(TreeSplitter, LocalizationService.Get("TreeSplitterTip"));
        var columnResizeTip = LocalizationService.Get("ResultColumnResizeTip");
        foreach (var splitter in new[] { NameColumnSplitter, SizeColumnSplitter, AllocatedColumnSplitter, ModifiedColumnSplitter })
        {
            ToolTipService.SetToolTip(splitter, columnResizeTip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(splitter, columnResizeTip);
        }
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
        });

        try
        {
            var result = await _scanner.ScanAsync(path, new ScanOptions
            {
                CalculateAllocatedSize = true,
                Parallelism = Math.Clamp(Environment.ProcessorCount, 2, 32),
                FollowReparsePoints = false
            }, progress, cancellation.Token);

            if (cancellation.IsCancellationRequested || requestId != Volatile.Read(ref _scanRequestId))
            {
                return;
            }

            _scanCache[path] = result;
            ActivateScanResult(result, loadedFromCache: false);
        }
        catch (OperationCanceledException)
        {
            if (requestId == Volatile.Read(ref _scanRequestId))
            {
                StatusText.Text = LocalizationService.Get("ScanCanceled");
                StatusDetailText.Text = string.Empty;
            }
        }
        catch (Exception exception)
        {
            if (requestId == Volatile.Read(ref _scanRequestId))
            {
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

    private void ActivateScanResult(ScanResult result, bool loadedFromCache)
    {
        _scanResult = result;
        _nodesByPath.Clear();
        _parentsByPath.Clear();
        IndexScanNodes(result.Root, parent: null);

        PopulateScan(result);
        ShowNode(result.Root);
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

    private void PopulateScan(ScanResult result)
    {
        RebuildDirectoryTree(result);
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
            var treeNode = CreateTreeNode(path, scannedNode, parentTreeNode is null ? drive : null);
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

    private TreeViewNode CreateTreeNode(string fullPath, ScanNode? node, DriveSummary? drive = null)
    {
        var treeNode = new TreeViewNode
        {
            Content = drive is null ? new FolderTreeRow(fullPath, node) : new FolderTreeRow(drive, node),
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
                    treeNode.Children.Add(CreateTreeNode(child.FullPath, child));
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

    private void IndexScanNodes(ScanNode node, ScanNode? parent)
    {
        var stack = new Stack<(ScanNode Node, ScanNode? Parent)>();
        stack.Push((node, parent));
        while (stack.TryPop(out var entry))
        {
            _nodesByPath[entry.Node.FullPath] = entry.Node;
            _parentsByPath[entry.Node.FullPath] = entry.Parent;
            for (var index = entry.Node.Children.Count - 1; index >= 0; index--)
            {
                stack.Push((entry.Node.Children[index], entry.Node));
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

        if (_currentNode is null)
        {
            ChildrenList.ItemsSource = null;
            return;
        }

        var query = FilterBox.Text.Trim();
        IEnumerable<ScanNode> nodes = _currentNode.Children;
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
            .Select(node => new NodeRow(node, displayAllocated, ResultColumns))
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
        if (sender is not FrameworkElement { Tag: string column })
        {
            return;
        }

        var minimum = GetResultColumnMinimum(column);
        var maximum = GetResultColumnMaximum(column);
        var width = Math.Clamp(GetResultColumnActualWidth(column) + e.HorizontalChange, minimum, maximum);
        SetResultColumnWidth(column, new GridLength(width));
    }

    private void ResultColumnSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string column })
        {
            FitResultColumn(column);
            e.Handled = true;
        }
    }

    private void FitResultColumn(string column)
    {
        var rows = ChildrenList.ItemsSource as IEnumerable<NodeRow> ?? [];
        var values = column switch
        {
            "Name" => rows.SelectMany(static row => new[] { row.Name, row.CountText }),
            "Size" => rows.Select(static row => row.SizeText),
            "Allocated" => rows.Select(static row => row.AllocatedText),
            "Modified" => rows.Select(static row => row.ModifiedText),
            _ => []
        };
        var header = column switch
        {
            "Name" => NameHeaderText.Text,
            "Size" => SizeHeaderText.Text,
            "Allocated" => AllocatedHeaderText.Text,
            "Modified" => ModifiedHeaderText.Text,
            _ => string.Empty
        };

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

        var padding = column == "Name" ? 24 : 30;
        SetResultColumnWidth(column, new GridLength(Math.Clamp(
            measured + padding,
            GetResultColumnMinimum(column),
            GetResultColumnMaximum(column))));
    }

    private double GetResultColumnActualWidth(string column) => column switch
    {
        "Name" => NameResultColumn.ActualWidth,
        "Size" => SizeResultColumn.ActualWidth,
        "Allocated" => AllocatedResultColumn.ActualWidth,
        "Modified" => ModifiedResultColumn.ActualWidth,
        _ => 0
    };

    private static double GetResultColumnMinimum(string column) => column switch
    {
        "Name" => 140,
        "Size" => 96,
        "Allocated" => 110,
        "Modified" => 135,
        _ => 80
    };

    private double GetResultColumnMaximum(string column)
    {
        var available = Math.Max(320, ResultHeaderGrid.ActualWidth);
        return column == "Name"
            ? Math.Max(GetResultColumnMinimum(column), available * 0.60)
            : Math.Max(GetResultColumnMinimum(column), Math.Min(320, available * 0.42));
    }

    private void SetResultColumnWidth(string column, GridLength width)
    {
        switch (column)
        {
            case "Name":
                ResultColumns.NameWidth = width;
                break;
            case "Size":
                ResultColumns.SizeWidth = width;
                break;
            case "Allocated":
                ResultColumns.AllocatedWidth = width;
                break;
            case "Modified":
                ResultColumns.ModifiedWidth = width;
                break;
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
            _scanCache.Clear();
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
            _scanCache.Clear();
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
        _navigationHistory.Clear();
        _navigationIndex = -1;
        RefreshButton.IsEnabled = false;
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
