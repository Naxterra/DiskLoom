# DiskLoom privacy

DiskLoom does not collect telemetry, analytics, file names, scan results, or personal data. Scans, snapshots, exports, duplicate hashes, settings, and logs remain on the local computer unless the user deliberately copies or uploads them.

The only network request DiskLoom makes is the update check: at most once per day at startup, and when **Check updates** is selected, DiskLoom asks the public GitHub Releases API of Naxterra/DiskLoom for the latest release, and if that release contains a `stable.json` update manifest, DiskLoom downloads it from the same release. The time of a check is recorded before the request, so a check that fails offline is not repeated at every start. The request contains ordinary HTTP metadata and a `DiskLoom/<version>` user agent; DiskLoom does not attach scan data, file names, or a device identifier. Set `"checkOnStartup": false` in `update-config.json` in the installation folder to turn the startup check off. Updates are downloaded and installed only after you confirm, only from that repository's release downloads, and only if the file matches the SHA-256 digest GitHub recorded and is a DiskLoom package of the offered version; when a publisher certificate is pinned into the build, the update must also be signed with it.

PDF export renders the report with Microsoft Edge WebView2, a component of Windows. DiskLoom loads only the local report file into it, but WebView2 itself may contact Microsoft services, for example to update itself, as it does for every app that uses it.

Crash details are appended locally to `%LOCALAPPDATA%\DiskLoom\crash.log`. They are never transmitted automatically.
