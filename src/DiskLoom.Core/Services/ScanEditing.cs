using DiskLoom.Core.Models;

namespace DiskLoom.Core.Services;

public static class ScanPaths
{
    // "D:" means "the current directory on D:" to Windows, and `"D:\"` on a command line arrives as `D:"`
    // because the backslash escapes the quote. Nobody asking to scan a drive means either, so both become D:\.
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
        {
            trimmed += Path.DirectorySeparatorChar;
        }
        return Path.GetFullPath(trimmed);
    }
}

// Several paths can point at the same file data (hard links). Its allocation is counted once, on the
// path that sorts first, so the attribution is the same in every scan and in every filtered view.
internal static class SharedStorage
{
    public static void Attribute(IEnumerable<ScanNode> sharedFiles)
    {
        foreach (var group in sharedFiles.GroupBy(static file => (file.VolumeSerialNumber, file.FileId)))
        {
            var owner = group.MinBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)!;
            foreach (var file in group)
            {
                var isOwner = ReferenceEquals(file, owner);
                file.AllocatedSize = isOwner ? file.PhysicalAllocatedSize : 0;
                file.IsAdditionalHardLink = !isOwner;
            }
        }
    }

    // Folder totals from their children, bottom-up and without recursion.
    public static void Aggregate(ScanNode root)
    {
        var stack = new Stack<(ScanNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.TryPop(out var item))
        {
            if (!item.Node.IsDirectory)
            {
                continue;
            }

            if (!item.Visited)
            {
                stack.Push((item.Node, true));
                foreach (var child in item.Node.Children)
                {
                    if (child.IsDirectory)
                    {
                        stack.Push((child, false));
                    }
                }
                continue;
            }

            long size = 0, allocated = 0, files = 0, folders = 0;
            foreach (var child in item.Node.Children)
            {
                size += child.Size;
                allocated += child.AllocatedSize;
                files += child.FileCount;
                folders += child.FolderCount + (child.IsDirectory ? 1 : 0);
            }
            item.Node.Size = size;
            item.Node.AllocatedSize = allocated;
            item.Node.FileCount = files;
            item.Node.FolderCount = folders;
        }
    }

    public static void SortBySize(ScanNode folder) => folder.Children.Sort(static (left, right) =>
    {
        var size = right.Size.CompareTo(left.Size);
        return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    });
}

// Applies deletions and moves to a finished scan so totals, statistics, charts, snapshots and exports
// stay correct without rescanning. Three steps so a UI can keep structural changes on its own thread:
// Detach (changes child lists), Recompute (sizes and statistics only), SortFolders (changes child order).
public static class ScanTreeEditor
{
    public static ScanNode? Find(ScanNode root, string path) => FindWithParent(root, path).Node;

    // Removes the nodes for these paths; returns the folders whose totals or order change.
    public static IReadOnlyList<ScanNode> Detach(ScanNode root, IEnumerable<string> paths)
    {
        var touched = new HashSet<ScanNode>(ReferenceEqualityComparer.Instance);
        foreach (var path in paths)
        {
            var (node, parent, ancestors) = FindWithParent(root, path);
            if (node is null || parent is null || !parent.Children.Remove(node))
            {
                continue;
            }
            touched.UnionWith(ancestors);
        }
        return touched.ToArray();
    }

    public static ScanResult Recompute(ScanResult result, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // A removed path may have carried the allocation of data that other links still use.
        SharedStorage.Attribute(result.Root.Files().Where(static file => file.SharesStorage));
        SharedStorage.Aggregate(result.Root);
        var (extensions, ages) = ScanStatistics.Build(result.Root, now, cancellationToken);
        return result with { Extensions = extensions, Ages = ages };
    }

    public static void SortFolders(IEnumerable<ScanNode> folders)
    {
        foreach (var folder in folders)
        {
            SharedStorage.SortBySize(folder);
        }
    }

    // Single-threaded convenience for callers without a UI.
    public static ScanResult RemovePaths(ScanResult result, IEnumerable<string> paths, DateTimeOffset now)
    {
        var touched = Detach(result.Root, paths);
        if (touched.Count == 0)
        {
            return result;
        }
        var updated = Recompute(result, now);
        SortFolders(touched);
        return updated;
    }

    private static (ScanNode? Node, ScanNode? Parent, IReadOnlyList<ScanNode> Ancestors) FindWithParent(ScanNode root, string path)
    {
        var target = path.TrimEnd('\\', '/');
        var rootPath = root.FullPath.TrimEnd('\\', '/');
        if (target.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return (root, null, []);
        }
        if (!target.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, []);
        }

        var ancestors = new List<ScanNode> { root };
        var current = root;
        ScanNode? parent = null;
        foreach (var segment in target[(rootPath.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Children.FirstOrDefault(child => child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return (null, null, []);
            }
            parent = current;
            current = next;
            if (current.IsDirectory)
            {
                ancestors.Add(current);
            }
        }
        // The node itself is not an ancestor of what was removed.
        if (current.IsDirectory)
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }
        return (current, parent, ancestors);
    }
}
