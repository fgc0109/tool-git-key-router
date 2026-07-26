# Architecture

## Layers

### GitKeyRouter.Core

Contains models, validation, SSH managed-block editing, URL rewrite comparison and reconciliation, backup contracts, diagnostics, and application services. It has no WinForms dependency and does not start external processes directly.

### GitKeyRouter.Infrastructure

Contains physical filesystem access, atomic JSON writes, bounded process execution, executable discovery, Git configuration access, safe logging, and backup persistence.

### GitKeyRouter.App

Contains the WinForms shell, controls, dialogs, CLI dispatcher, and manual composition root. GUI and CLI use the same `ApplicationServices` graph.

## Process and command safety

Git/OpenSSH commands use `ProcessStartInfo` with `UseShellExecute = false` and add every argument through `ArgumentList`. User-controlled namespaces, aliases, URLs, and paths are not interpolated into `cmd.exe` or PowerShell commands.

`ProcessRunner` reads stdout and stderr asynchronously with independent line limits and a per-line character limit. Truncated output preserves a head and tail summary and sets `StandardOutputTruncated` or `StandardErrorTruncated`. Results distinguish startup failure, user cancellation, timeout, non-zero exit, and process-tree termination failure (`KillFailed` / `TerminationError`).

Process execution tests launch the repository-owned `GitKeyRouter.ProcessTestChild` executable. Output modes produce fixed stdout/stderr or oversized lines; wait and process-tree modes atomically publish ready files before the test cancels or waits for timeout. This removes shell, network utility, and sub-100-ms scheduling assumptions while retaining a real Windows process-tree integration boundary.

`SafeFileLogger` sends messages and exception text through a bounded redaction pipeline before writing. Generated non-backtracking regular expressions remove private-key blocks, credential URL user-info, Authorization Bearer values, common password/token/secret and ASKPASS assignments, and prefixed GitHub/GitLab tokens. After the multiline private-key pass, credential matching is line-scoped to keep long logs linear. Plain URLs, paths, SSH public keys/fingerprints, and unprefixed commit SHA values remain readable.

## Application instance boundary

GUI startup and confirmed write commands (`apply --yes`, `apply-profiles --yes`) share a per-Windows-user mutex. Read-only, preview, diagnostic, parsing, and connection-test CLI commands do not take the exclusive lock. Version and help commands return before application-service construction.

The mutex prevents concurrent GitKeyRouter writers; it does not replace file/rewrite conflict detection for external editors, Git tools, or synchronization software.

## Application configuration persistence

`config.json` uses `System.Text.Json`. Saving writes a UTF-8 temporary file, flushes it, and moves it over the target. A malformed existing file is never automatically replaced.

Schema property names are read case-insensitively by application loading, backup metadata capture, and restore validation. Missing `SchemaVersion` remains compatible with Schema 1; duplicate, non-integer, invalid, or future schema values are rejected.

Configuration snapshots carry the source file's existence state and SHA-256 over its exact bytes. Ordinary service mutations use conditional save and reject a file that was created, removed, or replaced after load. A successful conditional save returns the new token so a later rollback can restore only if no third party has changed the just-written file.

## Git Profile transaction

`GitProfileService.ApplyAsync` performs the following persistent transaction:

1. Verify `git.exe` and read the exact global `include.path` sequence.
2. Reject a preview if the profile file set, existence state, or SHA-256 changed.
3. Persist all affected profile files, original existence, content hashes, and the ordered `include.path` values in a `prepared` transaction journal.
4. Generate every target file in a `.pending-*` staging directory and validate it with Git.
5. Recheck the preview token, persist and re-read the `applying` state, atomically write target files, remove stale profile files, and register the master include.
6. Re-read the final files and global includes, persist `committed`, and remove the completed journal.
7. On failure after mutation starts, restore the captured files and include sequence, verify the rollback, persist `rolled-back`, and remove the completed journal.

The journals live under `%APPDATA%\GitKeyRouter\git-profile-transactions`. A startup that holds the exclusive writer lock recovers any validated `applying` journal before constructing the GUI or dispatching a confirmed write command. Recovery failure keeps the journal and blocks the new writer. `prepared`, `committed`, and `rolled-back` journals cannot represent an unhandled live mutation and are removed best-effort.

## SSH Config editing and synchronization

Automated edits are based on exact marker pairs:

```text
# BEGIN GitKeyRouter managed block: <alias>
...
# END GitKeyRouter managed block: <alias>
```

The service locates an exact block range and replaces or removes only that range. Duplicate complete blocks are treated as errors.

Two synchronization modes are intentionally distinct:

- Conservative synchronization updates and adds configured identities while retaining orphan managed blocks.
- Strict synchronization also removes complete orphan GitKeyRouter managed blocks after an explicit diff and confirmation.

Strict mode does not delete ordinary `Host` entries, comments, unmanaged text, or incomplete marker fragments. SSH previews record original file existence and SHA-256 and are rejected if the file changes before apply.

## Git URL rewrite reconciliation

Expected rules are generated from enabled Service, Owner, and Repository routes and their identities. Current rules are read through `git config --global` and classified as correct, missing, duplicate, conflict, legacy, or extra.

Applying a plan captures every affected key's current ordered value set, computes the desired set, creates a safety snapshot, performs exact changes, and re-reads the result. Any remove, add, verification, or related application-config save failure restores the affected keys and configuration and reports apply and rollback errors separately. Plan-external Git keys are not modified.

Every generated plan records the exact ordered values for each affected key. `ApplyPlanAsync` compares only those keys before the safety snapshot and again immediately before mutation; a duplicate, order, case, addition, removal, or replacement change rejects the stale plan. Changes to plan-external keys are ignored and are never written or rolled back. Plans that also remove migrated application routes carry the configuration-file token and use conditional save/rollback.

## Default service routes

A service default identity derives a managed Service route with ID `service-default:<serviceId>`. Saving a default claims an older unmarked Service route; clearing the default removes the derived route while preserving Owner and Repository routes. More specific Git URL prefixes continue to take priority over a service-wide fallback.

## Backup and restore boundary

General safety snapshots persist application config, SSH Config, and exact Git URL rewrite pairs. Snapshot directories are prepared under `.pending-*`, integrity-checked, and moved into view only when complete.

The backup inventory classifies every direct child as complete, pending, damaged, unsupported, or unknown. Complete schema-2 snapshots pass manifest and file SHA-256 checks before being exposed for restore. Cleanup is a preview/apply operation constrained to invalid direct children; it re-scans before deletion and rejects complete snapshots, reparse points, active/recent pending directories, path escapes, and changed targets.

Git Profile files and the global `include.path` sequence are not part of this general backup format; they use the dedicated persistent transaction journal described above.

Application config, SSH Config, and Git URL rewrites are restored independently. Git rewrite restore performs exact Git configuration operations and never replaces the complete `.gitconfig` file.

See [Backup and restore](backup-and-restore.md) for the file format and current visibility limitations, and [Optimization status and roadmap](project-optimization-status.md) for remaining work.
