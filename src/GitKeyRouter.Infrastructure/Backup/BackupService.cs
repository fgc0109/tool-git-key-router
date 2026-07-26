using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.Configuration;

namespace GitKeyRouter.Infrastructure.Backup;

public sealed class BackupService : IBackupService
{
    private const string ManifestFileName = "manifest.json";
    private const string AppConfigFileName = "app_config.json";
    private const string SshConfigFileName = "ssh_config.txt";
    private const string GitRewritesFileName = "git_url_rewrites.json";
    private static readonly TimeSpan PendingCleanupGracePeriod = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IGitUrlRewriteStore _gitStore;
    private readonly IClock _clock;
    private readonly object _activePendingGate = new();
    private readonly HashSet<string> _activePendingDirectories = new(StringComparer.OrdinalIgnoreCase);

    public BackupService(IAppPaths paths, IFileSystem fileSystem, IGitUrlRewriteStore gitStore, IClock clock)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _gitStore = gitStore;
        _clock = clock;
    }

    public async Task<BackupManifest> CreateSnapshotAsync(string reason, CancellationToken cancellationToken = default)
    {
        _fileSystem.CreateDirectory(_paths.BackupRootDirectory);
        var finalDirectory = CreateUniqueDirectoryName();
        var pendingDirectory = Path.Combine(
            _paths.BackupRootDirectory,
            $".pending-{Guid.NewGuid():N}");
        _fileSystem.CreateDirectory(pendingDirectory);
        lock (_activePendingGate)
        {
            _activePendingDirectories.Add(Path.GetFullPath(pendingDirectory));
        }

        try
        {
            var appExists = _fileSystem.FileExists(_paths.ConfigPath);
            var sshExists = _fileSystem.FileExists(_paths.SshConfigPath);
            int? appConfigSchemaVersion = null;
            if (appExists)
            {
                var stagedAppPath = Path.Combine(pendingDirectory, AppConfigFileName);
                _fileSystem.CopyFile(_paths.ConfigPath, stagedAppPath, true);
                var appConfigText = await _fileSystem.ReadAllTextAsync(
                    stagedAppPath,
                    cancellationToken).ConfigureAwait(false);
                appConfigSchemaVersion = TryReadAppConfigSchemaVersion(appConfigText);
            }

            if (sshExists)
            {
                _fileSystem.CopyFile(
                    _paths.SshConfigPath,
                    Path.Combine(pendingDirectory, SshConfigFileName),
                    true);
            }

            IReadOnlyList<GitUrlRewriteRule> rewrites = [];
            string? gitCaptureError = null;
            try
            {
                rewrites = await _gitStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                gitCaptureError = exception.Message;
            }

            await _fileSystem.WriteAllTextAtomicAsync(
                Path.Combine(pendingDirectory, GitRewritesFileName),
                JsonSerializer.Serialize(rewrites, JsonOptions) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);

            var files = new Dictionary<string, BackupFileIntegrity>(StringComparer.OrdinalIgnoreCase);
            if (appExists)
            {
                files[AppConfigFileName] = await GetIntegrityAsync(
                    Path.Combine(pendingDirectory, AppConfigFileName),
                    cancellationToken).ConfigureAwait(false);
            }

            if (sshExists)
            {
                files[SshConfigFileName] = await GetIntegrityAsync(
                    Path.Combine(pendingDirectory, SshConfigFileName),
                    cancellationToken).ConfigureAwait(false);
            }

            files[GitRewritesFileName] = await GetIntegrityAsync(
                Path.Combine(pendingDirectory, GitRewritesFileName),
                cancellationToken).ConfigureAwait(false);

            var manifest = new BackupManifest
            {
                CreatedAt = _clock.UtcNow,
                Reason = reason,
                BackupDirectory = finalDirectory,
                ApplicationVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(),
                AppConfigExisted = appExists,
                AppConfigSchemaVersion = appConfigSchemaVersion,
                SshConfigExisted = sshExists,
                GitRewriteCount = rewrites.Count,
                GitRewriteCaptureError = gitCaptureError,
                Files = files
            };
            await _fileSystem.WriteAllTextAtomicAsync(
                Path.Combine(pendingDirectory, ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);

            await VerifyPreparedSnapshotAsync(
                pendingDirectory,
                finalDirectory,
                cancellationToken).ConfigureAwait(false);
            _fileSystem.MoveDirectory(pendingDirectory, finalDirectory);
            return manifest;
        }
        finally
        {
            lock (_activePendingGate)
            {
                _activePendingDirectories.Remove(Path.GetFullPath(pendingDirectory));
            }

            if (_fileSystem.DirectoryExists(pendingDirectory))
            {
                _fileSystem.DeleteDirectory(pendingDirectory, true);
            }
        }
    }

    public async Task<IReadOnlyList<BackupManifest>> ListAsync(CancellationToken cancellationToken = default)
        => (await InventoryAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.Status == BackupHealthStatus.Complete && item.Manifest is not null)
            .Select(item => item.Manifest!)
            .OrderByDescending(item => item.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<BackupInventoryItem>> InventoryAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<BackupInventoryItem>();
        foreach (var directory in _fileSystem.EnumerateDirectories(_paths.BackupRootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastWrite = GetLastWriteTimeUtc(directory);
            if (!IsDirectBackupChild(directory))
            {
                items.Add(InventoryItem(
                    directory,
                    BackupHealthStatus.Unknown,
                    "Directory is not a direct child of the backup root.",
                    lastWrite,
                    false));
                continue;
            }

            if (IsReparsePoint(directory))
            {
                items.Add(InventoryItem(
                    directory,
                    BackupHealthStatus.Unknown,
                    "Directory is a symbolic link, junction, or other reparse point and will not be traversed or cleaned.",
                    lastWrite,
                    false));
                continue;
            }

            if (Path.GetFileName(directory).StartsWith(".pending-", StringComparison.OrdinalIgnoreCase))
            {
                var active = IsActivePending(directory);
                var recent = _clock.UtcNow - lastWrite < PendingCleanupGracePeriod;
                var reason = active
                    ? "Snapshot creation is active in this process."
                    : recent
                        ? "Pending snapshot is recent and remains protected by the cleanup grace period."
                        : "Pending snapshot is older than the cleanup grace period and appears abandoned.";
                items.Add(InventoryItem(
                    directory,
                    BackupHealthStatus.Pending,
                    reason,
                    lastWrite,
                    !active && !recent));
                continue;
            }

            var path = Path.Combine(directory, ManifestFileName);
            if (!_fileSystem.FileExists(path))
            {
                items.Add(InventoryItem(
                    directory,
                    BackupHealthStatus.Unknown,
                    "Backup manifest is missing.",
                    lastWrite,
                    true));
                continue;
            }

            try
            {
                var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(text, JsonOptions)
                    ?? throw new InvalidDataException("Backup manifest is empty.");
                manifest.BackupDirectory = directory;
                if (manifest.SchemaVersion > 2)
                {
                    items.Add(InventoryItem(
                        directory,
                        BackupHealthStatus.Unsupported,
                        $"Backup schema {manifest.SchemaVersion} is newer than the supported schema 2.",
                        lastWrite,
                        true,
                        manifest));
                    continue;
                }

                if (manifest.SchemaVersion < 1)
                {
                    throw new InvalidDataException($"Backup schema {manifest.SchemaVersion} is invalid.");
                }

                var snapshot = await ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                var details = snapshot.Manifest.Files
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}: {item.Value.Length} bytes, SHA-256 {item.Value.Sha256}")
                    .ToList();
                if (details.Count == 0)
                {
                    details.Add("Legacy schema has no per-file integrity metadata.");
                }

                items.Add(InventoryItem(
                    directory,
                    BackupHealthStatus.Complete,
                    "Manifest and recorded file integrity checks passed.",
                    lastWrite,
                    false,
                    snapshot.Manifest,
                    details));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                items.Add(InventoryItem(
                    directory,
                    BackupHealthStatus.Damaged,
                    exception.Message,
                    lastWrite,
                    true));
            }
        }

        return items
            .OrderByDescending(item => item.Manifest?.CreatedAt ?? item.LastWriteTimeUtc)
            .ToList();
    }

    public async Task<BackupCleanupPlan> PreviewCleanupAsync(
        IEnumerable<string> backupDirectories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupDirectories);
        var requested = backupDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var inventory = await InventoryAsync(cancellationToken).ConfigureAwait(false);
        var plan = new BackupCleanupPlan();
        foreach (var path in requested)
        {
            if (!IsDirectBackupChild(path))
            {
                plan.Rejected.Add($"Outside backup root or not a direct child: {path}");
                continue;
            }

            var item = inventory.FirstOrDefault(candidate =>
                string.Equals(Path.GetFullPath(candidate.BackupDirectory), path, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                plan.Rejected.Add($"Directory was not found in the current inventory: {path}");
                continue;
            }

            if (!item.CanClean || item.Status == BackupHealthStatus.Complete)
            {
                plan.Rejected.Add($"Cleanup is not allowed for {item.Status}: {path} ({item.Reason})");
                continue;
            }

            plan.Targets.Add(new BackupCleanupTarget(
                path,
                item.Status,
                item.LastWriteTimeUtc,
                item.Reason));
        }

        return plan;
    }

    public async Task<OperationResult<IReadOnlyList<string>>> CleanAsync(
        BackupCleanupPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var deleted = new List<string>();
        var errors = new List<string>();
        foreach (var target in plan.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var currentPlan = await PreviewCleanupAsync([target.BackupDirectory], cancellationToken)
                    .ConfigureAwait(false);
                var current = currentPlan.Targets.SingleOrDefault();
                if (current is null
                    || current.Status != target.Status
                    || current.LastWriteTimeUtc != target.LastWriteTimeUtc)
                {
                    errors.Add($"Cleanup target changed after preview and was not deleted: {target.BackupDirectory}");
                    continue;
                }

                if (IsReparsePoint(target.BackupDirectory))
                {
                    errors.Add($"Cleanup target became a reparse point and was not deleted: {target.BackupDirectory}");
                    continue;
                }

                _fileSystem.DeleteDirectory(target.BackupDirectory, true);
                if (_fileSystem.DirectoryExists(target.BackupDirectory))
                {
                    errors.Add($"Cleanup target still exists after deletion: {target.BackupDirectory}");
                    continue;
                }

                deleted.Add(target.BackupDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors.Add($"Failed to delete '{target.BackupDirectory}': {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(
                deleted.Count == 0
                    ? "No invalid backup directories were cleaned."
                    : $"Cleaned {deleted.Count} invalid backup directories, but some targets failed.",
                errors.ToArray());
        }

        return OperationResult<IReadOnlyList<string>>.Ok(
            deleted,
            $"Cleaned {deleted.Count} invalid backup directories.");
    }

    public async Task<BackupSnapshot> ReadAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(backupDirectory, ManifestFileName);
        if (!_fileSystem.FileExists(manifestPath))
        {
            throw new FileNotFoundException("Backup manifest was not found.", manifestPath);
        }

        var manifestText = await _fileSystem.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestText, JsonOptions)
            ?? throw new InvalidDataException("Backup manifest is invalid.");
        manifest.BackupDirectory = backupDirectory;
        manifest.Files ??= new Dictionary<string, BackupFileIntegrity>(StringComparer.OrdinalIgnoreCase);

        await ValidateIntegrityAsync(backupDirectory, manifest, cancellationToken).ConfigureAwait(false);

        var appPath = Path.Combine(backupDirectory, AppConfigFileName);
        var sshPath = Path.Combine(backupDirectory, SshConfigFileName);
        var gitPath = Path.Combine(backupDirectory, GitRewritesFileName);
        var appText = _fileSystem.FileExists(appPath)
            ? await _fileSystem.ReadAllTextAsync(appPath, cancellationToken).ConfigureAwait(false)
            : null;
        var sshText = _fileSystem.FileExists(sshPath)
            ? await _fileSystem.ReadAllTextAsync(sshPath, cancellationToken).ConfigureAwait(false)
            : null;
        var gitText = _fileSystem.FileExists(gitPath)
            ? await _fileSystem.ReadAllTextAsync(gitPath, cancellationToken).ConfigureAwait(false)
            : "[]";
        var rewrites = JsonSerializer.Deserialize<List<GitUrlRewriteRule>>(gitText, JsonOptions) ?? [];
        return new BackupSnapshot
        {
            Manifest = manifest,
            AppConfigText = appText,
            SshConfigText = sshText,
            GitUrlRewrites = rewrites
        };
    }

    public async Task<OperationResult> RestoreAppConfigAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var readResult = await TryReadForRestoreAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        if (!readResult.Success || readResult.Value is null)
        {
            return OperationResult.Fail(readResult.Message, readResult.Errors.ToArray());
        }

        var snapshot = readResult.Value;
        if (snapshot.Manifest.AppConfigExisted)
        {
            if (snapshot.AppConfigText is null)
            {
                return OperationResult.Fail("The selected backup is missing its application configuration file.");
            }

            var validation = ValidateAppConfigForRestore(snapshot.AppConfigText);
            if (!validation.Success)
            {
                return validation;
            }
        }

        await CreateSnapshotAsync("Before restoring application configuration", cancellationToken).ConfigureAwait(false);
        if (!snapshot.Manifest.AppConfigExisted)
        {
            _fileSystem.DeleteFile(_paths.ConfigPath);
        }
        else if (snapshot.AppConfigText is not null)
        {
            await _fileSystem.WriteAllTextAtomicAsync(_paths.ConfigPath, snapshot.AppConfigText, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok("Application configuration restored.");
    }

    public async Task<OperationResult> RestoreSshConfigAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var readResult = await TryReadForRestoreAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        if (!readResult.Success || readResult.Value is null)
        {
            return OperationResult.Fail(readResult.Message, readResult.Errors.ToArray());
        }

        var snapshot = readResult.Value;
        await CreateSnapshotAsync("Before restoring SSH config", cancellationToken).ConfigureAwait(false);
        if (!snapshot.Manifest.SshConfigExisted)
        {
            _fileSystem.DeleteFile(_paths.SshConfigPath);
        }
        else if (snapshot.SshConfigText is not null)
        {
            await _fileSystem.WriteAllTextAtomicAsync(_paths.SshConfigPath, snapshot.SshConfigText, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok("SSH config restored.");
    }

    public async Task<OperationResult> RestoreGitRewritesAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var readResult = await TryReadForRestoreAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        if (!readResult.Success || readResult.Value is null)
        {
            return OperationResult.Fail(readResult.Message, readResult.Errors.ToArray());
        }

        var snapshot = readResult.Value;
        if (!string.IsNullOrWhiteSpace(snapshot.Manifest.GitRewriteCaptureError))
        {
            return OperationResult.Fail(
                "The selected backup does not contain a reliable Git URL rewrite snapshot.",
                snapshot.Manifest.GitRewriteCaptureError);
        }

        var safetyManifest = await CreateSnapshotAsync("Before restoring Git URL rewrites", cancellationToken).ConfigureAwait(false);
        var safetySnapshot = await ReadAsync(safetyManifest.BackupDirectory, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(safetySnapshot.Manifest.GitRewriteCaptureError))
        {
            return OperationResult.Fail(
                "Could not create a reliable safety snapshot before restoring Git URL rewrites.",
                safetySnapshot.Manifest.GitRewriteCaptureError);
        }

        var applyResult = await ReplaceGitRewritesAsync(snapshot.GitUrlRewrites, cancellationToken).ConfigureAwait(false);
        if (applyResult.Success)
        {
            return OperationResult.Ok("Git URL rewrites restored from the selected snapshot.");
        }

        var rollbackResult = await ReplaceGitRewritesAsync(safetySnapshot.GitUrlRewrites, cancellationToken).ConfigureAwait(false);
        if (rollbackResult.Success)
        {
            return OperationResult.Fail(
                "Git URL rewrite restore failed. The original rewrites were restored automatically.",
                [applyResult.Message, .. applyResult.Errors, $"Safety snapshot: {safetyManifest.BackupDirectory}"]);
        }

        return OperationResult.Fail(
            "Git URL rewrite restore failed, and the automatic rollback also failed.",
            [
                applyResult.Message,
                .. applyResult.Errors,
                rollbackResult.Message,
                .. rollbackResult.Errors,
                $"Safety snapshot: {safetyManifest.BackupDirectory}"
            ]);
    }

    private async Task<OperationResult> ReplaceGitRewritesAsync(
        IReadOnlyList<GitUrlRewriteRule> targetRules,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GitUrlRewriteRule> current;
        try
        {
            current = await _gitStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return OperationResult.Fail("Failed to read current Git URL rewrites.", exception.Message);
        }

        foreach (var rule in current.Distinct())
        {
            var result = await _gitStore.RemoveAllAsync(rule, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded && result.ExitCode != 5)
            {
                return OperationResult.Fail("Failed to remove an existing Git URL rewrite.", result.StandardError);
            }
        }

        foreach (var rule in targetRules)
        {
            var result = await _gitStore.AddAsync(rule, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return OperationResult.Fail("Failed to restore a Git URL rewrite.", result.StandardError);
            }
        }

        IReadOnlyList<GitUrlRewriteRule> actual;
        try
        {
            actual = await _gitStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return OperationResult.Fail("Failed to verify restored Git URL rewrites.", exception.Message);
        }

        if (!RulesEqual(actual, targetRules))
        {
            return OperationResult.Fail("Git URL rewrites did not match the requested state after restoration.");
        }

        return OperationResult.Ok("Git URL rewrites replaced.");
    }

    private async Task<OperationResult<BackupSnapshot>> TryReadForRestoreAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            return OperationResult<BackupSnapshot>.Ok(
                await ReadAsync(backupDirectory, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            return OperationResult<BackupSnapshot>.Fail(
                "The selected backup could not be validated and was not restored.",
                exception.Message);
        }
    }

    private async Task<BackupFileIntegrity> GetIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new BackupFileIntegrity
        {
            Length = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
    }

    private async Task VerifyPreparedSnapshotAsync(
        string pendingDirectory,
        string finalDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(pendingDirectory, ManifestFileName);
        var manifestText = await _fileSystem.ReadAllTextAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false);
        var persistedManifest = JsonSerializer.Deserialize<BackupManifest>(manifestText, JsonOptions)
            ?? throw new InvalidDataException("Prepared backup manifest is invalid.");
        persistedManifest.Files ??= new Dictionary<string, BackupFileIntegrity>(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(
                persistedManifest.BackupDirectory,
                finalDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Prepared backup manifest does not reference its final directory.");
        }

        await ValidateIntegrityAsync(
            pendingDirectory,
            persistedManifest,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateIntegrityAsync(
        string backupDirectory,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.SchemaVersion < 2)
        {
            return;
        }

        var expectedFiles = new List<string> { GitRewritesFileName };
        if (manifest.AppConfigExisted)
        {
            expectedFiles.Add(AppConfigFileName);
        }

        if (manifest.SshConfigExisted)
        {
            expectedFiles.Add(SshConfigFileName);
        }

        foreach (var fileName in expectedFiles)
        {
            if (!manifest.Files.TryGetValue(fileName, out var expected))
            {
                throw new InvalidDataException($"Backup integrity metadata is missing for '{fileName}'.");
            }

            var path = Path.Combine(backupDirectory, fileName);
            if (!_fileSystem.FileExists(path))
            {
                throw new InvalidDataException($"Backup file '{fileName}' is missing.");
            }

            var actual = await GetIntegrityAsync(path, cancellationToken).ConfigureAwait(false);
            if (actual.Length != expected.Length
                || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup file '{fileName}' failed its SHA-256 integrity check.");
            }
        }
    }

    private static bool RulesEqual(
        IReadOnlyList<GitUrlRewriteRule> actual,
        IReadOnlyList<GitUrlRewriteRule> expected)
        => NormalizeRules(actual).SequenceEqual(NormalizeRules(expected), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> NormalizeRules(IEnumerable<GitUrlRewriteRule> rules)
        => rules
            .Select(rule => $"{rule.ConfigKey}\n{rule.InsteadOfUrl}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

    private static int? TryReadAppConfigSchemaVersion(string text)
        => AppConfigSchemaReader.TryRead(text);

    private static OperationResult ValidateAppConfigForRestore(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var schemaVersion = AppConfigSchemaReader.Read(document.RootElement);
            if (schemaVersion < 1)
            {
                return OperationResult.Fail($"The backup application configuration has invalid schema version {schemaVersion}.");
            }

            if (schemaVersion > AppConfig.CurrentSchemaVersion)
            {
                return OperationResult.Fail(
                    $"The backup uses application configuration schema {schemaVersion}, but this version supports up to schema {AppConfig.CurrentSchemaVersion}.");
            }

            if (schemaVersion == AppConfig.CurrentSchemaVersion)
            {
                var config = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
                if (config is null)
                {
                    return OperationResult.Fail("The backup application configuration is empty.");
                }

                config.Normalize();
            }

            return OperationResult.Ok("Application configuration is compatible.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return OperationResult.Fail("The backup application configuration is invalid.", exception.Message);
        }
    }

    private static BackupInventoryItem InventoryItem(
        string directory,
        BackupHealthStatus status,
        string reason,
        DateTimeOffset lastWriteTimeUtc,
        bool canClean,
        BackupManifest? manifest = null,
        IReadOnlyList<string>? details = null)
        => new()
        {
            BackupDirectory = Path.GetFullPath(directory),
            Status = status,
            Reason = reason,
            LastWriteTimeUtc = lastWriteTimeUtc,
            CanClean = canClean,
            Manifest = manifest,
            Details = details ?? []
        };

    private bool IsDirectBackupChild(string path)
    {
        try
        {
            var root = Path.GetFullPath(_paths.BackupRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (_fileSystem.DirectoryExists(root) && IsReparsePoint(root))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(fullPath)?.FullName
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool IsActivePending(string path)
    {
        lock (_activePendingGate)
        {
            return _activePendingDirectories.Contains(Path.GetFullPath(path));
        }
    }

    private bool IsReparsePoint(string path)
    {
        try
        {
            return (_fileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        try
        {
            return _fileSystem.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private string CreateUniqueDirectoryName()
    {
        var baseName = _clock.LocalNow.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(_paths.BackupRootDirectory, baseName);
        var suffix = 1;
        while (_fileSystem.DirectoryExists(candidate))
        {
            candidate = Path.Combine(_paths.BackupRootDirectory, $"{baseName}-{suffix++}");
        }

        return candidate;
    }
}
