# Troubleshooting

## Inspect raw command results

Every Git, SSH, and ssh-keygen action exposes the executable path, arguments, stdout, stderr, exit code, duration, timeout state, cancellation state, and termination diagnostics.

### Output was truncated

`StandardOutputTruncated` or `StandardErrorTruncated` means the external program exceeded GitKeyRouter's bounded output retention. The result keeps a head and tail summary rather than every line. Re-run the command directly only if the retained diagnostic is insufficient, and review output for credentials before sharing it.

GitKeyRouter log files redact private-key blocks, credential URLs, Bearer values, common secret assignments, ASKPASS values, and prefixed GitHub/GitLab tokens. Plain paths, public keys/fingerprints, and commit SHA values remain for diagnosis. Redaction is a safety layer rather than a guarantee for every future credential format: review a log before sharing it outside your trusted environment.

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

Git URL rewrite plans record the exact ordered values for every affected key. A duplicate, order, case, addition, removal, or replacement change rejects the old plan; a change to an unrelated key does not. Application configuration saves similarly reject a file whose existence or SHA-256 changed after load. Reload the relevant page and review the new result instead of retrying the old plan.

## An interrupted Git Profile transaction cannot be recovered

The next GUI start or confirmed write command recovers a validated `applying` journal before performing new work. If journal validation, Git discovery, file restoration, or `include.path` restoration fails, GitKeyRouter leaves the journal under `%APPDATA%\GitKeyRouter\git-profile-transactions` and blocks the new writer.

Do not delete or edit that journal before preserving a copy. Resolve the reported Git/filesystem problem and retry GitKeyRouter; the same journal is deliberately retried on the next exclusive startup. A journal hash or path validation error means the recovery evidence is untrusted and requires manual inspection.

## Multiple executable candidates

Diagnostics lists every existing candidate found in PATH and common Windows locations. The first candidate in the documented lookup order is selected; no PATH or installation setting is changed.

## GitHub CLI identity routing is blocked

- Install GitHub CLI 2.40.0 or later. Older versions are rejected because their same-host account model cannot guarantee isolated multi-account routing.
- Run `GitKeyRouter.exe gh-status <identity-id-or-host-alias>` to verify the configured `AccountName` against `gh api user`.
- If no login exists, run `GitKeyRouter.exe gh-login <identity-id-or-host-alias>` and complete the browser flow with the expected account.
- If `hosts.yml` contains a plaintext `oauth_token`, repair the Windows credential store, remove the insecure GitHub CLI login through trusted GitHub CLI tooling, and log in again. GitKeyRouter deliberately does not read or migrate the Token value.
- Automatic routing rejects repositories whose remotes select different identities, whose selected HostAlias disagrees with the configured route, or whose tracking/push-default/origin remote cannot be determined. Use explicit `--identity` only after reviewing the repository target.
- `GH_TOKEN`, `GITHUB_TOKEN`, enterprise Token variables, and inherited `GH_REPO` do not override the routed child process. Pass the target through `-R/--repo` instead of environment variables.

Wrapped `gh auth`, `gh config`, `gh alias`, and user-supplied `--hostname` are blocked because they can mutate account selection or bypass the routed host. GitKeyRouter does not log forwarded arguments or output; review the live console before sharing it because the invoked GitHub CLI command may display repository data.

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

The backup page now lists every direct child of the backup root and shows complete, pending, damaged, unsupported, or unknown status. Only complete entries can be restored. Review the health reason and file-integrity details for an entry that previously appeared missing.

Cleanup requires a generated preview and confirmation. An active or one-hour grace-period pending directory, complete snapshot, reparse point, path outside the backup root, or target changed after preview is deliberately rejected. Resolve deletion permissions and refresh if cleanup reports a failure; do not bypass the boundary by manually targeting a linked directory.

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
