# Troubleshooting

## Inspect raw command results

Every Git, SSH, and ssh-keygen action exposes the executable path, arguments, stdout, stderr, exit code, duration, timeout state, cancellation state, and termination diagnostics.

### Output was truncated

`StandardOutputTruncated` or `StandardErrorTruncated` means the external program exceeded GitKeyRouter's bounded output retention. The result keeps a head and tail summary rather than every line. Re-run the command directly only if the retained diagnostic is insufficient, and review output for credentials before sharing it.

### Process timed out or was cancelled

- `TimedOut` means the configured timeout elapsed.
- `Cancelled` means the caller/user cancellation token was triggered.
- `StartException` means the executable could not be started.
- `KillFailed` or a non-empty `TerminationError` means GitKeyRouter could not prove that the process tree terminated after cancellation/timeout.

When termination fails, inspect Task Manager before retrying a write operation; the external Git/SSH process may still be active.

## Another GitKeyRouter instance is running

Exit code `4` is reserved for GUI startup or a confirmed write command (`apply --yes`, `apply-profiles --yes`) that cannot acquire the per-user exclusive lock. Read-only, preview, diagnostic, test, version, and help commands are allowed while the GUI is running.

Do not work around exit code `4` by deleting mutex-related system objects. Finish or close the active GitKeyRouter writer, then retry.

## Preview became stale

SSH Config and Git Profile previews record original existence and SHA-256. If a file is created, removed, or edited after preview, apply is rejected with a message asking you to regenerate the preview. Reload the page, review the new diff, and confirm again; do not overwrite the external change manually unless you have reviewed it.

Git URL rewrite plans do not yet carry an equivalent creation-time token. They are transactionally applied and rolled back, but affected values are captured when apply starts. Creation-time stale-plan rejection is planned for v0.4.11.

## Multiple executable candidates

Diagnostics lists every existing candidate found in PATH and common Windows locations. The first candidate in the documented lookup order is selected; no PATH or installation setting is changed.

## Git service SSH returns a non-zero exit code after success

This is normal for some `ssh -T git@host` tests. GitKeyRouter uses the selected GitHub, GitLab, Gitea, or generic provider adapter to classify service-specific authentication responses.

## A Git service reports `Key is invalid`

Git hosting SSH key forms expect a valid OpenSSH public-key line. RFC4716/SSH2 blocks, PEM/PKCS8 blocks, private keys, malformed Base64, and mismatched key algorithm blobs are not copied by the `Copy public key` action.

In the identities list, select the detected key variant and choose `Convert format` → `OpenSSH public key`. GitKeyRouter writes a separate `*.openssh.pub` file and preserves the source file. Existing target files are replaced only after explicit confirmation and backup.

PuTTY PPK files are detected but require PuTTYgen; GitKeyRouter does not parse or display private-key contents.

## Key generation or overwrite failed

New keys are generated at a unique temporary path and validated before live targets are backed up or replaced. Process failure, incomplete output, invalid public-key output, cancellation, replacement failure, or a target appearing concurrently leaves the original/newly appeared target in place or restores it from backup. Temporary generation files are cleaned up best-effort.

## Unmanaged SSH Host conflict

If a manually written `Host` uses the same alias as a GitKeyRouter identity, diagnostics reports a warning. The application does not delete or rewrite the manual entry.

## Duplicate or orphan managed block

A duplicate complete marker pair stops automatic updates for that alias rather than guessing which block is authoritative.

Conservative synchronization retains orphan managed blocks. Use strict synchronization only after reviewing its diff; strict mode removes complete orphan GitKeyRouter blocks but preserves ordinary `Host` entries, comments, unmanaged text, and incomplete marker fragments.

## A backup is missing from the list

The backup list hides `.pending-*`, directories without a manifest, and manifests that cannot be parsed. It does not verify every listed backup's data files and SHA-256 in advance, so a damaged but parseable snapshot can still appear and then fail when opened or restored. The current UI has no classified health inventory or safe cleanup action; inspect the backup root carefully and avoid deleting a directory while another operation is active.

## Restoring a backup reports an unsupported schema

GitKeyRouter validates application configuration before restore. `SchemaVersion` is read case-insensitively. A backup created by a newer release is rejected when its schema is higher than the running application supports; upgrade GitKeyRouter before restoring that snapshot.

## A URL is not rewritten

Check the **Git Rewrite Configuration** page for:

- A default identity that belongs to the selected service and its `service-default:<serviceId>` route.
- The expected Service, Owner, or Repository prefix and selected HostAlias.
- Missing, duplicate, conflict, legacy, or extra status.
- A longer prefix that legitimately takes priority.
- A password fallback or SSH authentication failure in the route test.

See [Optimization status and roadmap](project-optimization-status.md) for known remaining reliability work.
