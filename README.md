<p align="center"><img src="src/DiskLoom/Assets/DiskLoom-Mark.png" alt="DiskLoom logo" width="168" /></p>

# DiskLoom

[![Latest release](https://img.shields.io/github/v/release/Naxterra/DiskLoom)](https://github.com/Naxterra/DiskLoom/releases/latest)
[![License: Nax Source-Available](https://img.shields.io/badge/license-Nax%20Source--Available-blue.svg)](LICENSE)

Canonical repository: [github.com/Naxterra/DiskLoom](https://github.com/Naxterra/DiskLoom)

DiskLoom is a clean-room Windows 11 storage analyzer built with C#, .NET 11, WinUI 3, and the Windows App SDK. It is not affiliated with JAM Software and does not use TreeSize code, assets, branding, or UI designs.

## Install

Download the English or German x64 MSI or the portable ZIP from [GitHub Releases](https://github.com/Naxterra/DiskLoom/releases/latest). Both MSIs offer optional Start-menu and Desktop shortcuts and Windows Explorer folder/drive integration.

The reusable scanning engine `Naxterra.DiskLoom.Core` is attached to each release as a NuGet package (`.nupkg`).

## Included in 0.1

- Cancellable, 1–32 worker scanning with batched native allocation/file-ID enumeration on Windows and managed fallback for unsupported filesystems
- Logical size and Windows allocation-size (“size on disk”) accounting
- Safe default handling of junctions/symlinks, hidden/system files, exclusions, access failures, and files changing during a scan
- Fluent Windows 11 UI with Mica, system light/dark theme, folder tree, sortable details, treemap, and drive overview
- Results while scanning: top-level folders and their growing sizes appear within a second, long before a large drive finishes
- Share-of-parent bars in the folder tree and results list; drives show how full they are
- Configurable, reorderable result columns (size, size on disk, % of parent, files, folders, modified, created, last accessed, type, attributes), remembered between sessions
- Pie/ring and bar charts of a folder by subfolder, extension, file type, or age
- Tree filter by file type, name pattern, or age: folder sizes, tree, treemap, and charts then count only matching files
- Complete English and German UI with a persistent language switch under **Settings**
- Largest-file, extension, file-age, and review-first cleanup insights
- Duplicate detection with size grouping, first/last block sampling, then full SHA-256 verification
- Name/path filtering and whole-scan search by name wildcards, file type, size range, and modification date
- Open, Explorer reveal, Properties, copy path, Copy to…/Move to… (Explorer's own copy engine with progress, conflict prompts, and Undo), Recycle Bin, and explicit permanent deletion
- Compressed `.diskloom` snapshots and size-delta comparison
- CSV, JSON, Excel (.xlsx), HTML, and PDF export of the whole scan or the current folder, with a folder-depth limit and an option to leave out files
- `DiskLoom.Cli.exe` for scheduled/headless scans, exports, snapshots, and duplicate reports
- Self-contained x64 portable output and WiX MSI with selectable Start-menu/Desktop shortcuts, Apps & Features, major upgrades, and Explorer folder/drive context menus
- Update check against GitHub Releases with in-place install after you confirm: HTTPS only, SHA-256 checked against GitHub's asset digest, package identity and version checked, and Authenticode pinned when a publisher certificate is configured
- No subscription, telemetry, account, ads, or cloud dependency

## Deliberate v1 boundaries

The current scanner uses parallel Win32/.NET directory enumeration. Direct raw-NTFS MFT parsing, MTP devices, SSH, WebDAV URLs, and provider-specific cloud APIs are not presented as finished features. Mapped/cloud-synced folders and ordinary UNC/WebDAV-mounted paths work through the Windows filesystem. Owner/permission statistics are out of scope.

This distinction matters: DiskLoom 0.1 is a usable local/UNC replacement for the common disk-analysis workflow, not yet a claim of byte-for-byte parity with every feature accumulated by TreeSize Professional.

## Build

Requirements:

- Windows 11
- .NET 11 SDK (a release candidate while .NET 11 is pre-release)
- PowerShell 7.4+

```powershell
pwsh -File tools\Build-DiskLoom.ps1
pwsh -File tools\Build-DiskLoom.ps1 -Publish
pwsh -File tools\Build-DiskLoomInstaller.ps1
```

The ready-to-install English and German MSIs are written to `artifacts\DiskLoom\installer\DiskLoom-Setup-x64.msi` and `DiskLoom-Setup-x64-de-DE.msi`. The portable/self-contained build is under `artifacts\DiskLoom\win-x64`.

## CLI and scheduled scans

```powershell
DiskLoom.Cli.exe --scan "D:\Data" --export "D:\Reports\disk.csv" --snapshot "D:\Reports\disk.diskloom"
DiskLoom.Cli.exe --scan E:\ --export "D:\Reports\usb.xlsx" --depth 3 --no-files
DiskLoom.Cli.exe --scan "\\server\share" --duplicates "D:\Reports\duplicates.csv" --min-duplicate-mb 10
```

Run `DiskLoom.Cli.exe --help` for all switches. Use `tools\Register-DiskLoomScheduledScan.ps1` to register a daily or weekly Windows scheduled task after installation.

## GitHub update checks and updates

DiskLoom asks the public [Naxterra/DiskLoom GitHub Releases](https://github.com/Naxterra/DiskLoom/releases) API for the latest release at most once per day when it starts, and whenever **Check updates** is selected. The request is an ordinary HTTPS request with a `DiskLoom/<version>` user agent; it carries no scan data, file names, or device identifier. To turn the startup check off, set `"checkOnStartup": false` in `update-config.json` in the installation folder.

If a newer release exists, DiskLoom asks before doing anything. **Download and install** then installs the update in place and restarts DiskLoom, but only if all of these hold:

- the MSI comes from this repository's own GitHub release download URL over HTTPS;
- its SHA-256 matches the digest GitHub recorded for the uploaded asset;
- the package belongs to DiskLoom's MSI upgrade family and carries the offered version.

The release MSIs are not code-signed, so these checks establish that the file is the one published in this repository's release, not who built it: an attacker who controls the GitHub repository could publish a malicious release. That is the same trust as downloading the MSI from the release page by hand. When a publisher certificate is pinned (`-PublisherCertificateSha256` in `Build-DiskLoomInstaller.ps1`, for signed builds), the update must also carry a valid Authenticode signature from exactly that certificate.

For signed builds:

1. Build and sign the EXEs and MSI with `Build-DiskLoomInstaller.ps1 -CodeSigningCertificateThumbprint ...`.
2. Pass the SHA-256 fingerprint of that publisher certificate in `-PublisherCertificateSha256`. Updates still come only from this repository's release downloads; the certificate adds the signature check.
3. Upload the signed MSI to a GitHub release. GitHub's SHA-256 asset digest is used automatically when available.
4. Optionally generate and upload `stable.json` with `New-DiskLoomUpdateManifest.ps1`. It is used only when its version matches the release tag, so a file left over from an older release is ignored.

DiskLoom never installs an update without asking, and never installs one whose download hash differs, whose package is not DiskLoom's, or (with a pinned certificate) whose signature Windows rejects or that a different certificate signed.

## Uninstalling

Uninstalling DiskLoom in Apps & Features removes the program, its shortcuts and the Explorer menu entries. Your own settings and caches stay in `%LOCALAPPDATA%\DiskLoom` (column layout, language, the PDF export engine's data, downloaded updates and the crash log); delete that folder to remove them as well. A scheduled scan registered with `tools\Register-DiskLoomScheduledScan.ps1` is not part of the installation, so remove it in Task Scheduler (task name `DiskLoom Storage Report`) before or after uninstalling.

## Safety

- Directory reparse points are not followed by default, preventing cycles and accidental cross-volume scans.
- Duplicate results do not preselect or auto-delete anything.
- Cleanup insights are advisory only.
- Recycle and permanent delete are separate commands with confirmation.
- Access errors do not abort an otherwise useful scan and remain visible on the Issues tab.
- Updates install only after confirmation and only from this repository's releases (see above).
- Duplicate search never reads online-only cloud files (Nextcloud, OneDrive and other placeholders), because reading them would download them; it reports how many it skipped.
- Duplicate search skips files that changed after the scan, and deleting a duplicate is refused if the file changed since the check.
- Deleting or moving items updates the scan itself, so totals, charts, statistics, snapshots and exports match the disk without a rescan.
- Hard-linked data is counted once, always on the same path, also in filtered views. This relies on the file IDs that NTFS and ReFS report. In the rare folder that can only be listed without them, each hard link is counted.

## License

This software is under the Nax Source-Available License 1.0. You may install and use the official release builds, privately and at work, and you may read the source code. Modifying the software, redistributing it and selling it need the written permission of the owner. See [LICENSE](LICENSE).
