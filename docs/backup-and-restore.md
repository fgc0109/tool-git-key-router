# Backup and restore

## Snapshot scope

A general GitKeyRouter snapshot can contain:

- `app_config.json`: the application configuration, when it existed.
- `ssh_config.txt`: the user's SSH Config, when it existed.
- `git_url_rewrites.json`: the exact captured Git URL rewrite pairs.
- `manifest.json`: creation time, reason, source existence flags, configuration schema, Git capture status, and per-file length/SHA-256 metadata.

Git Profile files (`profiles.gitconfig`, `profile-*.gitconfig`) and the original global `include.path` sequence are not persisted in this format. Git Profile apply currently uses its own in-memory transaction and automatic rollback; it is not recoverable after a process crash or power loss.

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

`ListAsync` skips `.pending-*`, directories without `manifest.json`, and manifests that cannot be deserialized. It does not call full file-integrity validation while building the list. A parseable manifest whose data files are missing or modified can therefore appear in the UI and will be rejected when `ReadAsync` or a restore action validates it.

The current list is not a health inventory: some invalid directories are hidden, while other integrity failures are deferred until selection. The UI does not classify these states or provide a constrained cleanup action. Inspect the backup root manually if disk usage suggests abandoned directories, and do not delete an unknown directory while another GitKeyRouter operation may still be creating a snapshot. A classified inventory and safe cleanup workflow is planned for v0.4.12.

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
