# Changelog

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
