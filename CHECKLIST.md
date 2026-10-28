# What DiskLoom must never do

Every release report marks each item as verified by a named test in `tests/DiskLoom.Core.SmokeTests/Program.cs`, or as not verified.

1. DiskLoom must never delete or recycle anything without a confirmation that names exactly those items, and must never copy or move anything other than the items the user selected for it.
2. DiskLoom must never act on a different item than the one the user chose, for example `Backup` instead of `Backup ` when a name ends in a dot or space.
3. DiskLoom must never delete, recycle or move a drive root or a volume mounted in a folder.
4. DiskLoom must never delete every copy of a file from the duplicate list. At least one copy of each group that is still on disk and unchanged always stays, also when another program removed the other copies after the check.
5. DiskLoom must never delete a duplicate whose content may have changed since the duplicate check.
6. DiskLoom must never read the contents of online-only cloud files, because that downloads them and fills the disk the user is trying to clean.
7. DiskLoom must never write to, rename or change the files it scans or checks for duplicates. (Where last-access times are turned on, Windows itself updates them when a duplicate check reads a file.)
8. DiskLoom must never leave a half-written export, snapshot or settings file, and must never destroy the previous file when writing a new one fails or is canceled.
9. DiskLoom must never count the same data twice (hard links, or a folder reached through several links), and identical scans must never show different totals.
10. DiskLoom must never keep showing totals, charts, snapshots or exports that still include items it deleted or moved.
11. DiskLoom must never install an update without the user's confirmation, or an update that is not DiskLoom's MSI from this repository's GitHub release with a matching SHA-256 digest.
12. DiskLoom must never send scan data, file names or identifiers over the network. The only requests it makes are the update check against GitHub and the download of an update the user confirmed. (PDF export uses Microsoft Edge WebView2, which may contact Microsoft on its own, as it does for every app.)
13. DiskLoom must never leave program files, shortcuts, Explorer menu entries or registry entries behind after uninstalling. Only the user's own settings and caches in `%LOCALAPPDATA%\DiskLoom` stay, as the README states.
14. DiskLoom must never leave downloaded or partly downloaded installers behind once an update has run or failed.
15. DiskLoom must never follow directory links into a loop or count a folder twice.
16. DiskLoom must never close and lose the current scan because one command (a picker, a dialog, an export) failed.
