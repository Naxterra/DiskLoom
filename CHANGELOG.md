# Changelog

## 0.1.22 — 2026-09-27

- "Download and install" now updates DiskLoom in place instead of only opening the GitHub release page. Without a pinned signing certificate an update installs only if it is an MSI from this repository's own GitHub release, its SHA-256 matches the digest GitHub recorded for the upload, and the package is DiskLoom's (same upgrade code) with the offered version. DiskLoom restarts by itself once Windows Installer has finished.
- Updates now use the installer in the app's language (the German UI gets the German MSI); previously either MSI could be picked.
- Fixed a failed hash check reporting a file-in-use error and leaving the partial download behind.
- Update failures after choosing "Download and install" are now shown even when the offer came from the automatic startup check.

## 0.1.21 — 2026-09-27

- Added live results while scanning: the top-level folders and their growing sizes, file counts and treemap appear within a second instead of after the whole drive is done. Scan speed is unchanged (C:\, 600,000 files: 7.0 s with and without live results).
- Added share-of-parent bars: a "% of parent" column in the results list, bars in the folder tree, and fill bars for drives.
- Added configurable result columns: show or hide size, size on disk, % of parent, files, folders, modified, created, last accessed, type and attributes from the column header's right-click menu, reorder them under ⋯ → Customize columns, and sort by any of them. The choice is remembered.
- Added a Search tab that searches the whole scan (or only the current folder) by name wildcards, file type, size range and modification date; results have the full right-click menu.
- Added a tree filter (Filter in the command bar) by file type, name pattern or age. Folder sizes, the tree, the treemap, charts, largest files and insights then count only the matching files; snapshots always use the complete scan.
- Added a Charts tab with ring and bar charts of the current folder by subfolder, extension, file type or age; double-click a folder slice to open it.
- Added Copy to… and Move to… to the right-click menus of the result lists and the folder tree, using Explorer's own copy engine (progress, name-conflict prompts, Undo).
- Replaced the export file picker with an export dialog: CSV, JSON, Excel workbook (.xlsx, with an indented tree sheet and a file-type sheet), HTML report and PDF report, for the whole scan or the current folder, with a folder-depth limit and an option to leave out files. The CLI's `--export` accepts .xlsx and .html, with new `--depth` and `--no-files` switches.
- Fixed the treemap using only its minimum height instead of the whole tab.

## 0.1.20 — 2026-09-27

- Added a right-click menu to the results, largest files, insights and duplicates lists: Open, Show in File Explorer, Copy path, Properties, Select all, Move to Recycle Bin and Delete permanently. All four lists support Ctrl/Shift multi-select, and deleted items disappear from every list.
- Fixed the context menu acting on the previously selected row instead of the row that was right-clicked; right-clicking an unselected row now selects it first, as in Explorer.
- Summary insights (temporary and empty files) and drive roots can no longer be deleted from a context menu.
- Fixed drive-root snapshots storing paths relative to the app's working directory, which made Compare report false changes. Drive-root snapshots saved by 0.1.19 or earlier are rejected with an explanation; save a new one.
- Fixed files on different volumes being treated as hard links of each other (and missed by the duplicate finder) when a scan spans volumes.
- Fixed JSON export failing for folders nested 31 or more levels deep.
- The drive picker now shows one line (name and free space) with full details in a tooltip instead of a clipped second line; the path breadcrumb is vertically centered.
- Opening a large folder no longer checks every entry on disk (37,000 entries: 4.3 s → 7 ms).
- Saving and comparing snapshots is about 20× faster and, like export, no longer blocks the window.
- Less work on the UI thread after each scan: only folders are indexed, and the largest-files list and insights are computed in the background.
- Duplicate-search progress updates are rate-limited.

## 0.1.19 — 2026-09-13

- Enabled multi-select (Ctrl-click, Shift-click, and a Select all command) in the results list, and made Recycle/Delete/Copy path act on every selected item.
- Fixed vertical text alignment across every list in the app (results, largest files, extensions, ages, duplicates, insights, changes, issues, and the drive/folder pickers) — row content was pinned to the top of the row instead of centered.

## 0.1.18 — 2026-09-13

- Fixed followed reparse points (directory junctions/symlinks) double-counting a directory's files and size when the link's target was also reachable through the normal scan tree.
- Fixed a possible crash when starting a duplicate scan while a previous operation was still unwinding, by no longer disposing its cancellation token early.
- Debounced the results filter box so it no longer re-verifies every row's existence on disk on each keystroke, keeping large folders responsive while typing.

## 0.1.17 — 2026-08-30

- Enforced one DiskLoom instance with both a fixed application key and an OS-level named mutex shared across installed, portable, localized, renamed, and updated builds.
- Kept second-launch activation redirection so Explorer scan requests open in the existing window instead of being discarded.

## 0.1.16 — 2026-08-30

- Made a double-click anywhere on a folder-tree row expand or collapse that folder, while retaining the arrow control.
- Deferred the double-click expansion by one UI dispatcher turn to preserve the TreeView crash fix.

## 0.1.15 — 2026-08-30

- Cached completed scans for the current app session so switching back to a previously scanned drive is immediate.
- Kept Rescan as the explicit way to refresh a cached drive from disk.
- Replaced access-denied-only warning banners with a compact Problems tab count and an explanatory message inside that tab.
- Kept the warning banner for changed, missing, I/O, and other unexpected scan failures.
- Removed the recursive-results toggle that could materialize hundreds of thousands of rows and freeze the UI; folder navigation remains the fast way to inspect descendants.
- Vertically centered the editable path and breadcrumb controls with the surrounding navigation toolbar.

## 0.1.14 — 2026-08-30

- Fixed a native WinUI crash when selecting a populated folder such as NextCloud in the left tree.
- Avoided changing a TreeView node's expansion state from inside its `SelectionChanged` collection update.
- Deferred drive-triggered scan resets until the originating TreeView or drive-picker event has completed.

## 0.1.13 — 2026-08-30

- Made drive selection start scanning immediately, without requiring a separate click on Scan.
- Added every detected local drive as a persistent root in the left navigation tree; selecting an unscanned drive starts its scan.
- Added safe Cloud Files placeholder traversal so Nextcloud and comparable sync roots show their complete metadata without enabling junction or symbolic-link traversal.

## 0.1.12 — 2026-08-29

- Prevented duplicate Apps & Features entries when switching between equal-version English and German MSI packages.
- Made the upgrade remove all older DiskLoom product registrations in the shared upgrade family.
- Corrected the x64 default destination from `Program Files (x86)` to `Program Files`, while preserving user-selected paths in the full installer UI.
- Added a folder-tree context menu for Explorer, copy-path, Recycle Bin, and confirmed permanent-delete actions; drive roots remain protected.
- Left-aligned the top command bar and removed the large empty leading gap.
- Prevented re-entrant tree updates from interrupting a scan result, which could omit the final folder (such as NextCloud) and show a collection-modification error.

## 0.1.11 — 2026-08-26

- Added Back, Forward, and Up folder navigation with clickable path breadcrumbs and direct path editing.
- Synchronized the folder tree with every navigation source: results, tree, treemap, history, Up, and breadcrumbs now expand, select, and reveal the active folder.
- Added a wider, resizable folder pane and reduced card, toolbar, header, and row spacing for a denser wide-screen layout.
- Anchored targeted scans at the drive or share root in the folder tree and breadcrumbs, preserving the full navigation path above the scanned folder.
- Added Excel-style result columns: drag dividers to resize and double-click a divider to fit that column to its contents.
- Rebalanced the default table so Name no longer consumes most of a wide window.
- Enforced a single running app instance while redirecting Explorer scan requests to the existing window.
- Localized navigation, resizing hints, and missing-path errors, and clear stale notifications when a new scan begins.
- Added an explicit installer destination-folder page with a visible path, working Browse dialog, optional shortcut page, and Apps & Features install-location metadata.
- Replaced the flat gray UI with a logo-derived blue, cyan, teal, and violet palette in both light and dark modes.
- Made the folder-pane resize rail full-height with a visible grip and horizontal-resize cursor while retaining double-click reset.

## 0.1.10 — 2026-08-20

- Fixed Start-menu, Desktop, Apps & Features, and Explorer context-menu icons by using the dedicated multi-resolution DiskLoom ICO instead of an executable-derived installer icon.
- Localized Explorer scan commands for German Windows installations, including Germany, Austria, Switzerland, Luxembourg, and Liechtenstein.
- Added a fully localized German MSI, covering feature selection, progress, Files in Use, shortcut descriptions, and upgrade messages.
- Corrected the WixUI banner and dialog artwork safe areas so installer headings never overlap the DiskLoom branding.
- Enabled language-independent major upgrades so either MSI replaces an older English or German installation cleanly.

## 0.1.9 — 2026-08-18

- Removed the redundant “Size on disk” toggle beside Scan. Logical size is now the consistent primary measure; allocated size remains available in its dedicated summary and result column.
- Added sortable, aligned result columns and working folder navigation from both the tree and result list.
- Added a return path from scan issues to the previous results view.
- Improved scan throughput with batched native allocation and file-ID enumeration.
- Added complete English and German UI switching under Settings.
- Added application branding, GitHub-based update checks, optional installer shortcuts, and in-app creator information.
- Added Windows release automation for the MSI, portable ZIP, SHA-256 checksums, and the `Naxterra.DiskLoom.Core` GitHub Package.
