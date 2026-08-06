# GitKeyRouter

**English** | [简体中文](README.md)

[Project website](https://project-base-mirror.github.io/tool-git-key-router/) · [Download latest release](https://github.com/project-base-mirror/tool-git-key-router/releases/latest)

> **Collaboration note**
>
> A substantial part of GitKeyRouter's architecture, implementation, tests, documentation, and release workflow was developed collaboratively by the project author and ChatGPT (OpenAI). The project author defines requirements and product direction, reviews and accepts changes, and remains responsible for final design decisions, operation, and releases.

GitKeyRouter is a local desktop application for Windows 10 and Windows 11 that centrally manages:

- GitHub.com, GitLab.com, self-hosted GitLab, Gitea, and generic Git service instances
- Multiple SSH identities, accounts, and key paths for each service
- Service-specific HostAlias entries generated in `%USERPROFILE%\.ssh\config`
- Default-identity fallback routing for each Git service plus precise Owner / Repository routing through global `url.*.insteadOf` Git configuration
- Git Profiles that automatically select `user.name`, `user.email`, and signing keys by directory or remote URL
- Diffs, command output, backups, and selective restore before configuration changes
- GitHub CLI API identity isolation and selection from the repository SSH HostAlias
- A WinForms GUI and a simple CLI that can also be invoked by DevRunner

The project uses C#, .NET 10, and WinForms without a database, WebView, Node.js, or Electron. Core Git/SSH routing does not call hosting-provider APIs. The optional GitHub CLI wrapper only launches the user-installed `gh.exe`; GitHub CLI and the operating system own OAuth login and credential storage, and GitKeyRouter never handles the token.

> The target framework is .NET 10. Release builds and automated tests are validated on Windows. Before publishing, it is still recommended to run `dotnet build --configuration Release` and `dotnet test --configuration Release` on the target machine.

## Downloads and package choices

GitHub Releases provides Windows x64 packages:

- **`GitKeyRouter-v<version>-win-x64-setup.msi` (recommended)**: a self-contained installer including the .NET 10 runtime. It defaults to `C:\Program Files\GitKeyRouter\`, creates a Start menu entry, and offers an optional desktop shortcut.
- **`GitKeyRouter-v<version>-win-x64-framework-dependent-setup.msi`**: a smaller installer that requires the .NET 10 Desktop Runtime x64.
- **`GitKeyRouter-v<version>-win-x64-portable.zip`**: a self-contained portable build that includes the .NET runtime and can run after extraction.
- **`GitKeyRouter-v<version>-win-x64-framework-dependent.zip`**: a smaller framework-dependent build that requires the .NET 10 Desktop Runtime x64 on the target machine.
- **`SHA256SUMS.txt`**: SHA-256 checksums for both MSI and both ZIP packages.

Both MSI packages use a normal multi-file layout and support in-place upgrades, repair, and uninstall through Windows Installed apps. The wizard allows changing the install directory. If installation fails, look for the newest `MSI*.log` under `%TEMP%`. Both ZIP packages remain single-file applications for portable use.

Building from source requires the .NET 10 SDK. The self-contained MSI and ZIP do not require an SDK or preinstalled runtime for normal use.

## Safety and operational boundaries

GitKeyRouter is designed around convenient management, transparent state, and recoverable operations:

- It does not implement SSH key algorithms; key generation calls the system `ssh-keygen.exe`.
- Git operations call the actual `git.exe`.
- SSH tests call the actual `ssh.exe`.
- Git and SSH commands are not assembled through `cmd.exe` or PowerShell.
- External process arguments are passed separately through `ProcessStartInfo.ArgumentList`.
- Git, OpenSSH, and other software are never downloaded or installed automatically.
- Private-key contents are never stored, copied, or displayed.
- Deleting an identity record does not delete key files by default.
- Automatic SSH Config management modifies only GitKeyRouter managed blocks.
- Git rewrites are read and written precisely through `git config --global`; the complete `.gitconfig` is never replaced.
- Dangerous operations show a text diff or structured change plan first.
- A snapshot is created before changes, and restore operations create another safety snapshot before restoring.
- The GUI and write-capable CLI commands used with `--yes` share a single-instance lock for the current Windows user. Read-only, preview, diagnostic, test, version, and help commands remain available while the GUI is running.
- Private-key blocks, credential URLs, Bearer values, common secret/token/ASKPASS assignments, and prefixed GitHub/GitLab tokens are redacted from logs. Logs rotate at 5 MB by default, retain three historical files, and never interrupt the primary operation when logging fails.

## System requirements

- Windows 10 or Windows 11 x64
- .NET 10 SDK, only when building from source
- Git for Windows, providing `git.exe`
- Windows OpenSSH Client or Git for Windows OpenSSH, providing `ssh.exe` and `ssh-keygen.exe`

Git itself must use an OpenSSH backend. PuTTY/Plink does not read the OpenSSH `config`, `IdentityFile`, or `known_hosts` managed by GitKeyRouter. The application checks environment variables and Git configuration, then verifies the actual backend through a Git trace that connects only to a refused local port.

At startup and in the one-click diagnostics page, the application reports for each required tool:

- Whether it exists
- The selected executable path
- Other candidate paths
- The version or file version
- stdout, stderr, and exit code from the probe command

Missing tools produce a clear message. GitKeyRouter does not install them automatically.

## Quick start

### 1. Start the application

Starting without command-line arguments opens the WinForms interface:

```powershell
GitKeyRouter.exe
```

### 2. Configure Git services and identities

GitHub.com is a built-in service that cannot be deleted. For GitLab, Gitea, or another self-hosted service, first create an instance on the **Git Services** page and enter its host name, SSH user, port, and Web Base URL.

Then open **Git Identities** and create an identity such as:

```text
Git service: GitHub.com
Display name: Camus GitHub
Account: camus0109
HostAlias: github-camus
Private-key path: C:\Users\fgc01\.ssh\id_ed25519_github_camus
Public-key path: C:\Users\fgc01\.ssh\id_ed25519_github_camus.pub
Comment: camus0109
```

Application configuration is stored at:

```text
%APPDATA%\GitKeyRouter\config.json
```

Keys remain at the user-selected locations and are not copied into the application configuration directory.

### 3. Generate or import a key

The **Generate key** action calls:

```text
ssh-keygen.exe -t ed25519 -C <comment> -f <private-key-path> -N ""
```

The initial version creates keys without a passphrase by default, and the UI clearly warns about this before execution.

When a target file already exists, the user can:

- Cancel
- Return to identity editing and choose another filename
- Explicitly overwrite it; the new pair is first generated and validated at a unique temporary path, the old files are then backed up as `.gitkeyrouter.<timestamp>.<unique>.bak`, and only then are the live files replaced

If `ssh-keygen` fails, produces an incomplete pair, emits an invalid public key, is cancelled, live-file replacement fails, or another process creates a previously absent target during generation, GitKeyRouter does not overwrite the newly appeared file. Original keys remain in place or are restored from backup, and temporary files are cleaned up. After successful generation, the complete public key is displayed and can be copied or exported.

GitKeyRouter recognizes multiple public-key formats in the same identity directory and shows each format as a separate row on the **Git Identities** page:

- OpenSSH public key
- RFC4716 / SSH2 public key
- PEM / PKCS8 public key
- Unknown or invalid candidate public-key files

Format conversion never overwrites the source file. Explicit filenames are used side by side:

```text
id_ed25519_account.pub             # User-configured original public-key path
id_ed25519_account.openssh.pub     # OpenSSH
id_ed25519_account.rfc4716.pub     # RFC4716 / SSH2
id_ed25519_account.pem.pub         # PEM / PKCS8
```

If the target format file already exists, replacement is refused by default. When the user explicitly allows replacement, a `.gitkeyrouter.<timestamp>.bak` backup is created first. Backup and temporary conversion files are not shown in the public-key variant list.

Renaming key files updates every identity that shares those files and each corresponding SSH managed block. Every identity keeps the `HostName`, SSH port, and SSH user of its own Git service. If that service configuration is missing, preview fails instead of silently rewriting the block as GitHub.

The application never displays private-key contents. When an OpenSSH or PEM private key is selected, GitKeyRouter only calls `ssh-keygen -y` to derive a new `.openssh.pub` file. PuTTY PPK files must first be converted with PuTTYgen.

### 4. Add the public key to a Git service

Open the SSH Keys page for the relevant Git service account, create a new key, and use **Copy public key** on the key variant marked as OpenSSH. RFC4716, PEM, private-key, malformed Base64, and structurally invalid text are not copied by this action.

GitKeyRouter's key-management features do not call the GitHub, GitLab, or Gitea API and do not upload public keys on the user's behalf. The optional `gh` wrapper only forwards GitHub CLI operations and does not participate in public-key upload or token management.

### 5. Synchronize SSH Config

Synchronization adds only controlled blocks:

```sshconfig
# BEGIN GitKeyRouter managed block: github-camus
Host github-camus
    HostName github.com
    User git
    IdentityFile C:/Users/fgc01/.ssh/id_ed25519_github_camus
    IdentitiesOnly yes
# END GitKeyRouter managed block: github-camus
```

GitKeyRouter preserves:

- Other Host entries
- User comments
- Unmanaged text
- The existing CRLF or LF line-ending style

Normal synchronization does not rewrite the complete SSH Config. Full-text replacement happens only when the user explicitly opens **Edit raw text** and confirms the complete diff.

### 6. Configure default identities and repository routing

Every Git service can select a `DefaultIdentityId`; GitKeyRouter generates a service-wide fallback for repositories that do not match a more specific rule. For Gitea, `AccountName` represents the web-login account and is not assumed to be the repository Owner. For example:

```text
url.git@gitea-cloud:.insteadOf = git@git.policoil.top:
url.git@gitea-cloud:.insteadOf = ssh://git@git.policoil.top/
url.git@gitea-cloud:.insteadOf = git+ssh://git@git.policoil.top/
url.git@gitea-cloud:.insteadOf = https://git.policoil.top/
```

This preserves original Owners such as `project-base/*`, `game-riki/*`, and `game-hhmx/*` while routing all of them through the `gitea-cloud` HostAlias. Two independent Gitea services may share the same key files, but they must have separate service IDs, HostAliases, and HostNames.

GitHub may also have a default identity as a fallback for Owners or repositories without explicit routes. Longer, more specific Owner / Repository prefixes still take priority. Leave the default identity empty if only explicit multi-account routes are desired. For example:

```text
camus0109/*
→ github-camus

project-base-mirror/*
→ github-project-base
```

Routes derived from a default identity always use the managed ID `service-default:<serviceId>`. Saving a default identity claims an existing unmarked legacy service route; clearing the default removes that derived route without deleting Owner / Repository routes.

For `camus0109`, the expected rules are:

```text
url.git@github-camus:camus0109/.insteadOf = https://github.com/camus0109/
url.git@github-camus:camus0109/.insteadOf = git@github.com:camus0109/
```

The application executes operations equivalent to:

```powershell
git config --global --add "url.git@github-camus:camus0109/.insteadOf" "https://github.com/camus0109/"
git config --global --add "url.git@github-camus:camus0109/.insteadOf" "git@github.com:camus0109/"
```

Commands shown to the user are only for review and copying. The application never hands a complete command string to a shell.

## How repository routing works

Input:

```text
https://github.com/camus0109/panel-terraria.git
```

Git uses the longest matching `insteadOf` prefix and rewrites it to:

```text
git@github-camus:camus0109/panel-terraria.git
```

OpenSSH then selects the appropriate private key through `Host github-camus`.

Therefore:

- The Git service and Owner / Namespace select a HostAlias.
- The HostAlias selects an IdentityFile.
- Individual repositories do not need their own SSH-key configuration.

## Git rewrite states

The **Git Rewrite Configuration** page distinguishes:

- `Correct`: the exact rule exists once
- `Missing`: no rule exists for the prefix
- `Duplicate`: the same Base URL and insteadOf pair appears more than once
- `Conflict`: the same insteadOf prefix points to another Base URL
- `Extra`: a rule exists in Git but does not belong to an enabled repository route
- `LegacyAccountOwner`: a legacy Gitea route that treated the login account as the repository Owner and is waiting for user-confirmed migration

Supported actions include:

- Apply missing configuration
- Repair all current routes
- Delete a selected rule
- Remove duplicate rules
- Copy the corresponding Git command

Repair processes only the exact prefixes used by currently enabled routes. It does not automatically delete unrelated URL rewrites.

Deletion uses:

```text
git config --global --fixed-value --unset-all <key> <exact-value>
```

`--fixed-value` prevents Git from treating URLs as regular expressions.

## URL testing

### Local preview

The preview reads both the current Git rewrites and the expected rewrites derived from GitKeyRouter service configuration. It displays the actual match, expected match, missing or conflicting state, and final expected rewritten result. Previewing does not access the network.

### Real connection test

After explicit confirmation, GitKeyRouter runs:

```text
git ls-remote <original-url> HEAD
```

Before network access, GitKeyRouter confirms that Git actually uses OpenSSH. If PuTTY/Plink is detected, it reports the source. A Git-config-based selection can be switched to the detected OpenSSH after diff confirmation; an environment-variable override stops the test and lists the variables that must be cleared externally.

If a first connection fails with `Host key verification failed`, GitKeyRouter scans the server host public keys and displays their SHA-256 fingerprints. Only after the user verifies them through a trusted channel and confirms are they written to `%USERPROFILE%\.ssh\known_hosts`; the connection is then retried. Conflicting entries are never removed or replaced automatically.

The result window shows:

- The actual executable
- Separate arguments
- stdout
- stderr
- Exit code
- Timeout state and duration

## SSH testing

Normal mode:

```text
ssh -T git@github-camus
```

Verbose mode:

```text
ssh -vT git@github-camus
```

GitHub, GitLab, and Gitea SSH tests may return a non-zero exit code even after successful authentication. GitKeyRouter therefore uses the selected provider adapter to inspect service-specific success messages in stdout and stderr.

```text
successfully authenticated
```

Raw output is always available.

## Backup and restore

Backup directory:

```text
%APPDATA%\GitKeyRouter\backups\<timestamp>\
```

Each snapshot may contain:

```text
manifest.json
app_config.json
ssh_config.txt
git_url_rewrites.json
```

The files contain:

- `app_config.json`: Git services, identities, and repository routes
- `ssh_config.txt`: SSH Config before the change
- `git_url_rewrites.json`: all `url.*.insteadOf` rules before the change
- `manifest.json`: time, reason, configuration schema, whether original files existed, whether Git snapshot capture succeeded, and other metadata

If Git is unavailable, identity configuration can still be saved. The snapshot explicitly records that Git rewrite capture failed instead of pretending that an empty snapshot is valid. Such a snapshot cannot be used to restore Git rewrites.

The following can be restored independently:

- SSH Config
- Git URL rewrites
- Application configuration

Git rewrite restore still removes and adds exact rules through `git config`; it never replaces the complete `.gitconfig`.

## Git Profiles and commit identity

Version 0.3.0 introduced the **Git Profiles** page. Each profile can store `user.name`, `user.email`, a signing key, a default Git service, and a default SSH identity. Directory and remote-URL rules determine where a profile applies.

Directory rules generate Git's official `includeIf "gitdir/i:<directory>/"` condition. Remote URL rules generate `includeIf "hasconfig:remote.*.url:<pattern>"`. GitKeyRouter does not edit every repository's `.git/config`; instead, it generates one managed conditional-config entry and separate profile files under `%APPDATA%\GitKeyRouter\git-profiles`, then registers a single `include.path` in global Git configuration.

**Preview and apply** shows diffs for the entry file and every profile file before writing. After deleting a profile or rule, apply again to remove previously generated conditional configuration.

## CLI

The GUI and CLI share the same service graph and business logic.

```powershell
GitKeyRouter.exe diagnose
GitKeyRouter.exe list-services
GitKeyRouter.exe list-identities
GitKeyRouter.exe list-profiles
GitKeyRouter.exe list-routes
GitKeyRouter.exe apply
GitKeyRouter.exe apply --yes
GitKeyRouter.exe apply-profiles
GitKeyRouter.exe apply-profiles --yes
GitKeyRouter.exe parse-url ssh://git@gitlab.example:2222/company/platform/repo.git
GitKeyRouter.exe resolve-profile C:\code\work\repo --url https://gitlab.example/company/repo.git
GitKeyRouter.exe test-service gitlab-office
GitKeyRouter.exe test-route camus0109
GitKeyRouter.exe test-route company/platform --service gitlab-office
GitKeyRouter.exe test-route camus0109 --url https://github.com/camus0109/panel-terraria.git
GitKeyRouter.exe test-route camus0109 --url https://github.com/camus0109/panel-terraria.git --connect
GitKeyRouter.exe test-ssh github-camus
GitKeyRouter.exe test-ssh github-camus --verbose
GitKeyRouter.exe ssh-backend
GitKeyRouter.exe ssh-backend --use-openssh
GitKeyRouter.exe ssh-backend --use-openssh --yes
GitKeyRouter.exe trust-host gitea-cloud
GitKeyRouter.exe trust-host gitea-cloud --yes
GitKeyRouter.exe gh-login github-camus
GitKeyRouter.exe gh-status github-camus
GitKeyRouter.exe gh-status --all --json
GitKeyRouter.exe gh-logout github-camus --yes
GitKeyRouter.exe gh-resolve --json
GitKeyRouter.exe gh-resolve -R project-base-mirror/tool-git-key-router
GitKeyRouter.exe gh -- release view
GitKeyRouter.exe gh --identity github-camus -- release create v1.0.0
GitKeyRouter.exe gh -- release create v1.0.0 -R camus0109/example
GitKeyRouter.exe version
GitKeyRouter.exe help
```

`apply` displays the SSH diff and Git rewrite plan by default. Changes are executed only with `--yes`. `apply-profiles` follows the same policy and displays the conditional Git Config diff by default.

The GUI and confirmed `apply`, `apply-profiles`, `ssh-backend`, and `trust-host` writes share the exclusive lock to prevent cross-process changes. Their preview modes and other read-only CLI commands do not acquire it. `version` / `--version` and `help` / `--help` return before configuration loading or application-service construction, so scripts and release validation can use them while the GUI is running.

`test-route --connect` also requires a real `--url`, preventing the application from sending network requests for an invented repository.

### GitHub CLI multi-account routing

`gh-login` gives each GitHub identity a separate
`%APPDATA%\GitKeyRouter\github-cli\<identity-id-hash>\` directory and forces browser login.
After login, GitKeyRouter runs `gh api user --jq .login` in the same `GH_CONFIG_DIR` and
requires the returned login to match the identity's `AccountName`. `gh-status` performs the
same verification, while `gh-status --all --json` checks every configured GitHub identity.
After the first successful verification, the directory receives an `identity.json` manifest
containing only the stable ID, HostAlias, host, and expected account. A mismatched manifest
blocks directory reuse and never contains a token. `gh-logout <identity> --yes` logs out only
the named host and account while retaining the directory for diagnostics.

`gh -- ...` selects an identity in this order: explicit `--identity`, forwarded `-R/--repo`,
the current branch's `pushRemote`, `remote.pushDefault`, its tracking remote, then `origin`.
Automatic mode reads every expanded push URL for the selected remote; all of those URLs must
resolve to one identity and repository. Unselected fork or upstream remotes produce diagnostic
warnings instead of blocking the selected target. HTTPS remotes fall back only when repository
routing selects one identity. A HostAlias/route mismatch or ambiguous result still fails closed.

`gh-resolve` is a read-only preflight command. It reports the selected `gh.exe` path, version,
and source plus the repository root, remote decision source, push URLs, HostAlias, identity, and
final `GH_HOST` / `GH_REPO`. `--json` provides a stable automation surface. The command does not
create a credential directory or verify an account.

The wrapper requires GitHub CLI 2.40.0 or later, sets the target `GH_CONFIG_DIR`, `GH_HOST`,
and resolved `GH_REPO` (or removes it when there is no repository context), and removes `GH_TOKEN`,
`GITHUB_TOKEN`, enterprise token variables, Git repository paths, SSH/ASKPASS settings, and Git
configuration overrides from the child process.
It never calls global `gh auth switch`, and GitKeyRouter does not log forwarded arguments or
output. Wrapped `gh auth`, `gh config`, `gh alias`, and user-supplied `--hostname` are blocked;
use `gh-login`, `gh-logout`, and `gh-status` for account lifecycle operations. Credential health checks bound the size of `hosts.yml`, reject
reparse points, and detect both quoted and unquoted plaintext `oauth_token` keys. The identity
is blocked without reading or printing the token value.

Each identity directory has an independent cross-process reader/writer lock. Ordinary commands
for a registered identity hold a shared lock through verification and child execution, so they can
run concurrently. First registration, login, logout, and `gh extension` operations hold the exclusive
lock; different identities do not block one another. The service's safe execution receipt contains
only identity, host, repository, `gh` version, exit code, duration, and lock mode—never arguments,
output, or credentials.

GitHub CLI configuration and credentials are excluded from GitKeyRouter configuration,
snapshots, and portable backups. Run `gh-login` again for each identity after restore or
migration; GitHub CLI and the operating system's secure credential store remain responsible
for the credential.

CLI diagnostic exit codes:

- `0`: no warnings or errors
- `1`: warnings exist
- `2`: errors exist or a connection test failed
- `3`: invalid arguments or an application execution failure
- `4`: GUI startup or a `--yes` write command requires the exclusive lock while another GUI or write operation is already running for the current Windows user

## Configuration example

```json
{
  "SchemaVersion": 4,
  "GitServices": [
    {
      "Id": "github.com",
      "DisplayName": "GitHub.com",
      "ProviderKind": "GitHub",
      "HostName": "github.com",
      "SshPort": null,
      "SshUser": "git",
      "WebBaseUrl": "https://github.com",
      "AllowInsecureHttp": false,
      "EnableExtendedSshUrlRewrites": false,
      "IsBuiltIn": true
    }
  ],
  "Identities": [
    {
      "Id": "7b90999f7ce643fbb07eb4b94f802579",
      "ServiceInstanceId": "github.com",
      "DisplayName": "Camus GitHub",
      "AccountName": "camus0109",
      "HostAlias": "github-camus",
      "PrivateKeyPath": "C:\\Users\\fgc01\\.ssh\\id_ed25519_github_camus",
      "PublicKeyPath": "C:\\Users\\fgc01\\.ssh\\id_ed25519_github_camus.pub",
      "EmailOrComment": "camus0109",
      "CreatedAt": "2026-07-18T08:30:45+00:00"
    }
  ],
  "RepositoryRoutes": [
    {
      "ServiceInstanceId": "github.com",
      "NamespacePath": "camus0109",
      "IdentityId": "7b90999f7ce643fbb07eb4b94f802579",
      "Enabled": true
    }
  ]
}
```

## Configuration upgrades

The current configuration schema is Schema 4. The `SchemaVersion` property name is read case-insensitively, so forms such as `schemaVersion` are not mistaken for Schema 1; application loading, backup manifests, and restore validation share the same rule. A missing property still migrates as Schema 1, while duplicate, non-integer, invalid, or future values are rejected without modifying the source file. Reading an older configuration preserves all services, identities, key paths, repository routes, and Git Profiles. For any service that has a default identity but no managed service route, normalization derives a service-wide route. Legacy Gitea account-level rewrites are not deleted automatically; they are converted only after user-confirmed migration. GitHub Owner routing remains compatible. A snapshot is still created before modifying configuration.

## Input validation

GitHub Owners and HostAliases use:

```regex
^[A-Za-z0-9_.-]+$
```

Additional restrictions:

- GitHub Owners cannot contain slashes; GitLab, Gitea, and generic services allow multi-level Namespaces separated by `/`.
- HostAliases cannot contain spaces, slashes, colons, wildcards, or control characters.
- A HostAlias cannot directly equal the real host name of a configured service.
- Every identity must have a unique HostAlias.
- One enabled Namespace in the same Git service can point to only one identity.
- A route identity must belong to the same Git service.
- Private- and public-key paths must be different absolute paths.

## Local build and validation

In Windows PowerShell with the .NET 10 SDK installed:

```powershell
dotnet restore .\GitKeyRouter.sln --locked-mode
dotnet restore .\src\GitKeyRouter.App\GitKeyRouter.App.csproj `
  -r win-x64 `
  --locked-mode `
  -p:NuGetLockFilePath=packages.publish-win-x64.lock.json `
  -p:PublishSingleFile=true
dotnet format .\GitKeyRouter.sln --verify-no-changes --no-restore
dotnet build .\GitKeyRouter.sln -c Release --no-restore
dotnet test .\GitKeyRouter.sln -c Release --no-build --no-restore
dotnet publish .\src\GitKeyRouter.App\GitKeyRouter.App.csproj `
  -c Release `
  -r win-x64 `
  --no-restore `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:EnableCompressionInSingleFile=true
```

You can also double-click or run the following from the solution root:

```text
Publish-WinX64.bat
Publish-WinX64-SelfContained.bat
Publish-WinX64-FrameworkDependent.bat
```

All three call `scripts\Publish-WinX64.ps1` and use the same formatting, build, test, publish, and executable-validation pipeline. Each BAT file prints the repository and output directories, opens the actual output folder after success, and keeps the window open with an error message after failure. `Publish-WinX64.bat` also builds and validates both WiX MSI variants, then creates two versioned MSI packages, two ZIP files, and `SHA256SUMS.txt` under `artifacts\release`.

To temporarily skip tests:

```powershell
.\scripts\Publish-WinX64.ps1 -SkipTests
```

Final output directories:

```text
artifacts\publish\win-x64\                         # Self-contained build with GitKeyRouter.exe
artifacts\publish\win-x64-framework-dependent\     # Framework-dependent build with GitKeyRouter.exe
artifacts\installer-payload\                        # Multi-file payloads dedicated to both installers
artifacts\installer\                                # Both structurally validated MSI packages
artifacts\release\                                  # Both MSI, both ZIP, and SHA256SUMS.txt
```

Installer validation reads the MSI database and checks the version, upgrade identity, install directory, shortcuts, uninstall metadata, payload files, and runtime boundary. After publishing, the manual `Installer lifecycle` GitHub Actions workflow can exercise silent install, optional cross-version upgrade, installed-version smoke, and uninstall cleanup for both variants while retaining logs as workflow artifacts.

These directories contain local generated artifacts and are ignored by `.gitignore`. They are not copied by commits or branch merges. When publishing from an isolated workspace, artifacts exist only in that workspace; run the BAT file again from the current repository root to create them under the current repository's `artifacts` directory.

## Test isolation

Process operations are abstracted behind interfaces, and most tests use in-memory objects or temporary directories.

ProcessRunner output, timeout, cancellation, and process-tree tests use a repository-owned child executable and ready-file handshakes. They do not depend on `cmd.exe`, `ping`, or 100 ms scheduling guesses.

Both test projects use the xUnit v3 executable test-project model while retaining the VSTest adapter and existing TRX output. `Directory.Packages.props` centrally owns the test SDK, xUnit, and adapter versions; project files no longer repeat package versions, and lock files continue to provide repeatable restores.

Git integration tests set:

```text
GIT_CONFIG_GLOBAL=<temporary-file>
```

This isolates global configuration and does not modify the developer machine's real Git configuration.

## Common errors

### `git.exe` cannot be found

Install Git for Windows or add it to `PATH`. GitKeyRouter does not install it automatically.

### `ssh.exe` or `ssh-keygen.exe` cannot be found

Enable OpenSSH Client in Windows Optional Features, or verify that the Git for Windows OpenSSH directory exists.

### Manual `ssh -vT` succeeds but Git for Windows still fails

Run `GitKeyRouter.exe ssh-backend`. If it reports PuTTY/Plink or TortoisePlink, Git and the manual OpenSSH command are using different configuration and host-key stores. Run `ssh-backend --use-openssh` to preview the repair and add `--yes` only after review. If `GIT_SSH_COMMAND`, `GIT_SSH`, or `GIT_SSH_VARIANT` is reported as a blocker, select OpenSSH in Git for Windows or remove the override, then fully exit and restart GitKeyRouter.

Git Extensions, TortoiseGit, and other GUIs may inject PuTTY/Plink settings only into Git processes that they launch. GitKeyRouter cannot read another process's private environment. If GitKeyRouter's real connection test succeeds and only one Git GUI fails, select OpenSSH in that GUI's SSH settings and restart the GUI as well.

### `Permission denied (publickey)`

Check:

1. Whether the public key was added to the correct Git service account
2. Whether `IdentityFile` in SSH Config points to the correct private key
3. Whether the HostAlias matches the Base URL used by the repository-route rewrite
4. Whether the private-key file exists

### `Could not resolve hostname github-camus`

This usually means SSH Config does not contain `Host github-camus`, or the managed block has not been synchronized.

### `Host key verification failed`

This is not a private-key password request. It is either first-use server identity confirmation or an existing-key conflict. The GUI connection test displays scanned SHA-256 fingerprints; the CLI can preview them with `GitKeyRouter.exe trust-host <id-or-host>`. Verify them through an administrator or another trusted channel before adding `--yes`. GitKeyRouter never automatically deletes or replaces a conflicting host key.

### A URL is not rewritten

On the **Git Rewrite Configuration** page, check:

- Whether the Git service has a default identity that belongs to it and a corresponding `service-default:<serviceId>` route
- Whether current rules match the expected service-wide rules
- Whether HTTPS / SSH insteadOf entries are `Correct`, missing, or waiting for legacy-route migration
- Whether a longer or conflicting prefix exists
- Whether the input URL contains the complete path prefix for the Namespace

### `config.json` is malformed

The application stops saving and displays the JSON parsing error. It does not automatically overwrite a malformed file. Repair it manually or restore it from **Backup and Restore**.

## Project structure

```text
src/
  GitKeyRouter.App/             WinForms, CLI, and UI orchestration
  GitKeyRouter.Core/            Models, validation, business services, and diagnostics
  GitKeyRouter.Infrastructure/  Git, SSH, process, file, and backup implementations

tests/
  GitKeyRouter.Tests/           Unit tests and isolated Git integration tests
```

## Documentation and design

- [Architecture](docs/architecture.md)
- [Backup and restore](docs/backup-and-restore.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Optimization status and roadmap (Chinese)](docs/project-optimization-status.md)
- [Version record index (Chinese)](docs/version-records.md)
- [Security policy](SECURITY.md)
- [Contributing guide](CONTRIBUTING.md)
- [Chinese README](README.md)

## License

This project is licensed under the [MIT License](LICENSE).
