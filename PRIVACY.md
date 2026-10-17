# DiskLoom privacy

DiskLoom does not collect telemetry, analytics, file names, scan results, or personal data. Scans, snapshots, exports, duplicate hashes, settings, and logs remain on the local computer unless the user deliberately copies or uploads them.

The only network request is the update check: at most once per day at startup, and when **Check updates** is selected, DiskLoom asks the public GitHub Releases API of Naxterra/DiskLoom for the latest release. The request contains ordinary HTTP metadata and a `DiskLoom/<version>` user agent; DiskLoom does not attach scan data, file names, or a device identifier. Set `"checkOnStartup": false` in `update-config.json` in the installation folder to turn the startup check off. Updates are downloaded and installed only after you confirm, only from that repository's release downloads, and only if the file matches the SHA-256 digest GitHub recorded and is a DiskLoom package of the offered version; when a publisher certificate is pinned into the build, the update must also be signed with it.

Crash details are appended locally to `%LOCALAPPDATA%\DiskLoom\crash.log`. They are never transmitted automatically.
