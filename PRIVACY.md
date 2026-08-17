# DiskLoom privacy

DiskLoom does not collect telemetry, analytics, file names, scan results, or personal data. Scans, snapshots, exports, duplicate hashes, settings, and logs remain on the local computer unless the user deliberately copies or uploads them.

The only optional network request is an HTTPS update-manifest check when a distributor configures an update URL in `update-config.json`. The request contains ordinary HTTP metadata; DiskLoom does not attach scan data or a device identifier. Update installers must match the manifest SHA-256 hash and a publisher certificate pinned into the installed build.

Crash details are appended locally to `%LOCALAPPDATA%\DiskLoom\crash.log`. They are never transmitted automatically.
