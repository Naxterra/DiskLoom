# Changelog

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
