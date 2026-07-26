# Backup and restore

## Snapshot scope

A general GitKeyRouter snapshot can contain:

- `app_config.json`: the application configuration, when it existed.
- `ssh_config.txt`: the user's SSH Config, when it existed.
- `git_url_rewrites.json`: the exact captured Git URL rewrite pairs.
- `manifest.json`: creation time, reason, source existence flags, configuration schema, Git capture status, and per-file length/SHA-256 metadata.

Git Profile files (`profiles.gitconfig`, `profile-*.gitconfig`) and the original global `include.path` sequence are not persisted in this general snapshot format. Git Profile apply uses a separate journal under `%APPDATA%\GitKeyRouter\git-profile-transactions`, containing the exact affected files, existence state, content hashes, original contents, and ordered `include.path` values. An interrupted `applying` transaction is rolled back when the next exclusive GitKeyRouter writer starts.

Do not treat the transaction directory as user-managed backup history. Completed journals are removed automatically. If validation or recovery fails, the journal remains in place and new writes are blocked so that the recovery evidence is not overwritten.

## Atomic snapshot publication

`BackupService.CreateSnapshotAsync` does not write directly into the final timestamp directory:

1. Create `.pending-<guid>` under the backup root.
2. Capture application config, SSH Config, and Git rewrite pairs.
3. Write the staged data files.
4. Compute file lengths and SHA-256 values and write `manifest.json`.
5. Re-read the manifest and every required file and verify metadata and hashes.
6. Move the prepared directory to its unique final timestamp name on the same volume.

Failure, cancellation, integrity mismatch, or publication failure triggers best-effort removal of the pending directory. A final directory is visible only after the move succeeds.

## Listing and integrity

The inventory scans every direct child of the backup root and classifies it as:

- `Complete`: supported manifest and recorded file-integrity checks passed.
- `Pending`: snapshot publication has not completed; active and one-hour grace-period entries are protected.
- `Damaged`: manifest parsing, required-file, length, SHA-256, or payload validation failed.
- `Unsupported`: the manifest schema is newer than this GitKeyRouter version.
- `Unknown`: no usable manifest exists, or the directory is a reparse point or outside the accepted boundary.

The UI shows the classification, reason, files, and integrity metadata. Only complete backups can be opened or restored.

Cleanup always has a separate preview and confirmation. The service accepts only direct children of the configured backup root and re-scans the selected target immediately before deletion. Complete backups, active or recent pending directories, symbolic links/junctions/other reparse points, path escapes, and targets whose status or timestamp changed after preview are rejected. Partial deletion failures are reported and the remaining directory stays visible on refresh.

## Restore validation

Before changing current state, restore operations:

- Read and validate the selected manifest.
- Verify required files against their recorded lengths and SHA-256 values.
- Validate application-config JSON and its case-insensitive schema version.
- Reject a schema newer than the running application supports.
- Reject Git rewrite restore when capture failed or was recorded as unreliable.
- Create a new safety snapshot of the current state.

Application config, SSH Config, and Git rewrites are restored independently so the user can choose the required scope.

## Missing-source semantics

The manifest records whether application config and SSH Config existed when the snapshot was taken. Restoring a snapshot where one of those files did not exist removes the current file after the new safety snapshot is created.

Git rewrite restore removes and re-adds exact rewrite pairs through Git. It never copies or replaces the complete global `.gitconfig`.

## Rollback and verification

Git rewrite restore captures the pre-restore rule set. If applying the selected snapshot fails, it attempts to restore the pre-restore rules and verifies the final exact set. Apply and rollback failures are reported separately.

The legacy convenience file `%USERPROFILE%\.ssh\config.gitkeyrouter.bak` is refreshed immediately before an SSH Config write. Timestamped snapshot directories and their integrity metadata remain the authoritative recovery history.
