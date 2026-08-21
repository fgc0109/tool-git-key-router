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

`ProcessRunner` reads stdout and stderr asynchronously with independent line limits and a per-line character limit. Truncated output preserves a head and tail summary and sets `StandardOutputTruncated` or `StandardErrorTruncated`. Results distinguish startup failure, user cancellation, timeout, non-zero exit, and process-tree termination failure (`KillFailed` / `TerminationError`). Explicit passthrough requests can instead inherit the attached console for interactive tools; these requests can suppress argument retention and still preserve the child exit code.

Process execution tests launch the repository-owned `GitKeyRouter.ProcessTestChild` executable. Output modes produce fixed stdout/stderr or oversized lines; wait and process-tree modes atomically publish ready files before the test cancels or waits for timeout. This removes shell, network utility, and sub-100-ms scheduling assumptions while retaining a real Windows process-tree integration boundary.

`SafeFileLogger` sends messages and exception text through a bounded redaction pipeline before writing. Generated non-backtracking regular expressions remove private-key blocks, credential URL user-info, Authorization Bearer values, common password/token/secret and ASKPASS assignments, and prefixed GitHub/GitLab tokens. After the multiline private-key pass, credential matching is line-scoped to keep long logs linear. Plain URLs, paths, SSH public keys/fingerprints, and unprefixed commit SHA values remain readable.

## Test platform and dependency management

Both test projects use the xUnit v3 executable test-project model (`OutputType=Exe`) and the xUnit VSTest adapter, preserving the existing `dotnet test` and TRX workflow. Package versions are declared once in the root `Directory.Packages.props`; individual project files contain version-free `PackageReference` entries, and checked-in lock files pin the resolved graph for normal and `win-x64` restores.

xUnit v3 analyzer rule `xUnit1051` is temporarily suppressed in both test projects because existing operation-specific cancellation and timeout tests intentionally use dedicated tokens. The suppression is explicit technical debt for the replanned v0.4.27 analyzer cleanup; all other compiler and analyzer warnings remain errors when `CI=true`.

## GitHub CLI identity boundary

GitHub CLI routing is optional and supports `gh` 2.40.0 or later. The effective Git push URL, explicit `-R/--repo`, or an explicit identity resolves to one configured GitHub identity. Automatic remote precedence is branch `pushRemote`, `remote.pushDefault`, branch tracking remote, then `origin`. Every push URL on the selected remote must resolve to the same identity and repository; unselected remotes are diagnostic context rather than automatic blockers. SSH HostAlias evidence must agree with the most specific enabled Service, Owner, or Repository route. HTTPS remotes are accepted only when that route uniquely identifies the account.

`gh-resolve` exposes this decision as text or JSON without creating a credential directory or making a GitHub API request. Toolchain results include the selected executable path, discovery source, version probe, and existing candidates so PATH shadowing and unsupported versions are visible before authentication.

Each identity receives a `GH_CONFIG_DIR` derived from a SHA-256 hash of its stable identity ID. The child process also receives the resolved `GH_HOST` and, when available, the exact `GH_REPO`. Inherited GitHub token variables, repository paths, SSH/ASKPASS overrides, system/global Git config paths, and numbered `GIT_CONFIG_KEY_*` / `GIT_CONFIG_VALUE_*` entries are removed. GitKeyRouter never calls `gh auth switch`.

After the first successful account verification, GitKeyRouter atomically registers an `identity.json` manifest containing schema version, stable identity ID, service host, HostAlias, and expected account. Existing v0.4.17 directories are adopted only after `gh api user --jq .login` succeeds. A malformed, oversized, reparse-point, or mismatched manifest blocks the directory before `gh` starts. The manifest never contains credentials.

Every identity directory has a cross-process reader/writer file lock. Ordinary commands for an already registered identity hold a shared lock through account verification and child execution, so independent reads can run concurrently. First-time manifest adoption, browser login, logout, and `gh extension` operations use the exclusive lock; different identities use different lock files. Manifest validation is repeated after an exclusive waiter acquires the lock.

After login and before every wrapped command, a captured `gh api user --jq .login` probe must match the configured `AccountName`. The probe arguments and output are not logged. Forwarded commands inherit the console, omit arguments from their process result, and return the original `gh` exit code. The service result includes a safe receipt containing identity, host, repository, `gh` version, exit code, duration, and lock mode, but never forwarded arguments, output, or credentials.

GitHub CLI owns OAuth and credential persistence. GitKeyRouter bounds credential metadata inspection to 1 MiB, rejects a reparse-point `hosts.yml`, and scans only for quoted or unquoted `oauth_token` keys; it never reads the value and blocks the identity when plaintext fallback is detected. GitHub CLI directories are outside application configuration, snapshots, and portable backup payloads. `gh-logout` removes only the configured account through GitHub CLI and deletes the non-secret identity manifest after success.

## Application instance boundary

GUI startup and confirmed write commands (`apply --yes`, `apply-profiles --yes`, `ssh-backend --use-openssh --yes`, and `trust-host --yes`) share a per-Windows-user mutex. Read-only, preview, diagnostic, parsing, and connection-test CLI commands do not take the exclusive lock. Version and help commands return before application-service construction.

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

## Git SSH backend and host trust

Managed SSH aliases are an OpenSSH feature, so Git routing has a hard compatibility boundary: the Git child process must use OpenSSH. `GitSshBackendService` resolves explicit environment and Git configuration first. When neither identifies the backend, it executes a bounded Git probe against the refused local endpoint `127.0.0.1:1` with tracing enabled and classifies the actual SSH child command. This probe does not contact an external host and disables terminal/ASKPASS interaction.

PuTTY/Plink is blocked before a repository connection test. A confirmed repair writes only global `core.sshCommand` and `ssh.variant`, rechecks the preview immediately before mutation, verifies the resolved backend afterward, and attempts to restore the prior ordered global values if either write or verification fails. Environment overrides and unknown custom wrappers are not changed automatically.

`SshHostTrustService` is separate from user-key authentication. It obtains server public keys with the `ssh-keyscan.exe` located beside the selected OpenSSH tools, validates their key blobs, and computes SHA-256 fingerprints in process. Trust is a preview/apply operation over both the scanned key set and the `known_hosts` existence/SHA-256 token. Conflicting existing keys fail closed. Confirmed writes append only the reviewed endpoint keys, preserve the existing newline style, create a byte-preserving backup when applicable, then re-read through `ssh-keygen -F`; verification failure rolls back the file. The service never disables strict host verification.

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

## Windows packaging boundary

Portable delivery and installed delivery deliberately use different layouts. Both portable ZIP variants contain a single-file `GitKeyRouter.exe`; the installer pipeline publishes an ordinary multi-file payload so Windows Installer can service individual application and runtime files reliably. The recommended MSI is self-contained, while the framework-dependent MSI omits the .NET runtime and requires the .NET 10 Desktop Runtime x64.

Both MSI variants share one stable upgrade identity, install per machine under `ProgramFiles64Folder`, register uninstall metadata, create a Start menu shortcut, and make the desktop shortcut optional. Release construction reads the MSI database to verify product/version metadata, the install-directory dialog, shortcut and registry tables, multi-file contents, and the expected presence or absence of `coreclr.dll`. The installer lifecycle workflow additionally supports install, cross-version upgrade, installed `--version` smoke, and uninstall verification on a Windows runner. Signing is not implied by MSI packaging and remains a separate supply-chain requirement.

See [Backup and restore](backup-and-restore.md) for the file format and current visibility limitations, and [Optimization status and roadmap](project-optimization-status.md) for remaining work.
