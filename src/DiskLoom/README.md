<p align="center"><img src="https://raw.githubusercontent.com/Naxterra/DiskLoom/main/src/DiskLoom/Assets/DiskLoom-Mark.png" alt="DiskLoom logo" width="168" /></p>

# DiskLoom

Canonical repository: [github.com/Naxterra/DiskLoom](https://github.com/Naxterra/DiskLoom)

DiskLoom is a clean-room Windows 11 storage analyzer built with C#, .NET 11, WinUI 3, and the Windows App SDK. It is not affiliated with JAM Software and does not use TreeSize code, assets, branding, or UI designs.

## Included in 0.1

- Cancellable, 1–32 worker scanning with batched native allocation/file-ID enumeration on Windows and managed fallback for unsupported filesystems
- Logical size and Windows allocation-size (“size on disk”) accounting
- Safe default handling of junctions/symlinks, hidden/system files, exclusions, access failures, and files changing during a scan
- Fluent Windows 11 UI with Mica, system light/dark theme, folder tree, sortable details, treemap, and drive overview
- Complete English and German UI with a persistent language switch under **Settings**
- Largest-file, extension, file-age, and review-first cleanup insights
- Duplicate detection with size grouping, first/last block sampling, then full SHA-256 verification
- Name/path filtering, recursive search, Explorer reveal, copy path, Recycle Bin, and explicit permanent deletion
- Compressed `.diskloom` snapshots and size-delta comparison
- Complete CSV and JSON export
- `DiskLoom.Cli.exe` for scheduled/headless scans, exports, snapshots, and duplicate reports
- Self-contained x64 portable output and WiX MSI with selectable Start-menu/Desktop shortcuts, Apps & Features, major upgrades, and Explorer folder/drive context menus
- Opt-in auto-update client: HTTPS only, SHA-256 checked, Windows Authenticode chain checked, and publisher-certificate pinned
- No subscription, telemetry, account, ads, or cloud dependency

## Deliberate v1 boundaries

The current scanner uses parallel Win32/.NET directory enumeration. Direct raw-NTFS MFT parsing, MTP devices, SSH, WebDAV URLs, and provider-specific cloud APIs are not presented as finished features. Mapped/cloud-synced folders and ordinary UNC/WebDAV-mounted paths work through the Windows filesystem. PDF/Excel reports and owner statistics are also roadmap items; CSV/JSON and snapshots are implemented now.

This distinction matters: DiskLoom 0.1 is a usable local/UNC replacement for the common disk-analysis workflow, not yet a claim of byte-for-byte parity with every feature accumulated by TreeSize Professional.

## Build

Requirements:

- Windows 11
- .NET 11 SDK (Preview 7 while .NET 11 is pre-release)
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
DiskLoom.Cli.exe --scan "\\server\share" --duplicates "D:\Reports\duplicates.csv" --min-duplicate-mb 10
```

Run `DiskLoom.Cli.exe --help` for all switches. Use `tools\Register-DiskLoomScheduledScan.ps1` to register a daily or weekly Windows scheduled task after installation.

## GitHub update checks and signed updates

DiskLoom checks the public [Naxterra/DiskLoom GitHub Releases](https://github.com/Naxterra/DiskLoom/releases) API once per day and whenever **Check updates** is selected. If a newer release is present, unsigned builds open the verified GitHub release page. Direct in-app installation remains intentionally disabled until a trusted code-signing identity is configured.

For signed automatic installation:

1. Build and sign the EXEs and MSI with `Build-DiskLoomInstaller.ps1 -CodeSigningCertificateThumbprint ...`.
2. Pass the SHA-256 fingerprint of that publisher certificate in `-PublisherCertificateSha256` and `https://github.com/Naxterra/DiskLoom/releases/latest/download/stable.json` in `-UpdateManifestUrl`.
3. Upload the signed MSI to a GitHub release. GitHub's SHA-256 asset digest is used automatically when available.
4. Optionally generate and upload `stable.json` with `New-DiskLoomUpdateManifest.ps1`; it takes precedence when present.

The app will never install a remote update when the feed is blank, the download hash differs, Windows rejects the Authenticode chain, or the signing certificate differs from the pinned certificate.

## Safety

- Directory reparse points are not followed by default, preventing cycles and accidental cross-volume scans.
- Duplicate results do not preselect or auto-delete anything.
- Cleanup insights are advisory only.
- Recycle and permanent delete are separate commands with confirmation.
- Access errors do not abort an otherwise useful scan and remain visible on the Issues tab.
- Remote updates cannot fall back to unsigned packages.
