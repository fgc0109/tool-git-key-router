using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Services;

public sealed class SshKeyRenameService
{
    private readonly IFileSystem _fileSystem;
    private readonly IAppConfigStore _configStore;
    private readonly IBackupService _backupService;
    private readonly SshConfigService _sshConfigService;
    private readonly SshKeyService _sshKeyService;
    private readonly IClock _clock;

    public SshKeyRenameService(
        IFileSystem fileSystem,
        IAppConfigStore configStore,
        IBackupService backupService,
        SshConfigService sshConfigService,
        SshKeyService sshKeyService,
        IClock clock)
    {
        _fileSystem = fileSystem;
        _configStore = configStore;
        _backupService = backupService;
        _sshConfigService = sshConfigService;
        _sshKeyService = sshKeyService;
        _clock = clock;
    }

    public async Task<OperationResult<SshKeyRenamePlan>> BuildPlanAsync(
        string identityId,
        string newBaseName,
        CancellationToken cancellationToken = default)
    {
        var nameError = ValidateBaseName(newBaseName);
        if (nameError is not null)
        {
            return OperationResult<SshKeyRenamePlan>.Fail("The new key filename is invalid.", nameError);
        }

        var configSnapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = configSnapshot.Config;
        var identity = config.Identities.FirstOrDefault(item =>
            string.Equals(item.Id, identityId, StringComparison.OrdinalIgnoreCase));
        if (identity is null)
        {
            return OperationResult<SshKeyRenamePlan>.Fail("Identity was not found.");
        }

        if (!_fileSystem.FileExists(identity.PrivateKeyPath))
        {
            return OperationResult<SshKeyRenamePlan>.Fail(
                "The configured private key does not exist and cannot be renamed.",
                identity.PrivateKeyPath);
        }

        var privateDirectory = Path.GetDirectoryName(identity.PrivateKeyPath);
        var publicDirectory = Path.GetDirectoryName(identity.PublicKeyPath);
        if (string.IsNullOrWhiteSpace(privateDirectory) || string.IsNullOrWhiteSpace(publicDirectory))
        {
            return OperationResult<SshKeyRenamePlan>.Fail("The configured key paths do not have valid parent directories.");
        }

        var originalPrivatePath = Path.GetFullPath(identity.PrivateKeyPath);
        var originalPublicPath = Path.GetFullPath(identity.PublicKeyPath);
        var newPrivatePath = Path.Combine(privateDirectory, newBaseName);
        var publicStem = SshKeyService.GetPublicKeyVariantStem(identity.PublicKeyPath);
        var configuredPublicName = Path.GetFileName(identity.PublicKeyPath);
        var configuredSuffix = configuredPublicName.StartsWith(publicStem, StringComparison.OrdinalIgnoreCase)
            ? configuredPublicName[publicStem.Length..]
            : ".pub";
        var newPublicPath = Path.Combine(publicDirectory, newBaseName + configuredSuffix);

        if (PathsEqual(originalPrivatePath, newPrivatePath))
        {
            return OperationResult<SshKeyRenamePlan>.Fail("The new filename is the same as the current private-key filename.");
        }

        var variantsResult = await _sshKeyService.ListPublicKeyVariantsAsync(identity, cancellationToken).ConfigureAwait(false);
        if (!variantsResult.Success || variantsResult.Value is null)
        {
            return OperationResult<SshKeyRenamePlan>.Fail(variantsResult.Message, variantsResult.Errors.ToArray());
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [originalPrivatePath] = Path.GetFullPath(newPrivatePath),
            [originalPublicPath] = Path.GetFullPath(newPublicPath)
        };
        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [originalPrivatePath] = Path.GetFullPath(newPrivatePath)
        };

        foreach (var variant in variantsResult.Value)
        {
            var fileName = Path.GetFileName(variant.Path);
            var suffix = fileName.StartsWith(publicStem, StringComparison.OrdinalIgnoreCase)
                ? fileName[publicStem.Length..]
                : Path.GetExtension(fileName);
            var destination = Path.Combine(Path.GetDirectoryName(variant.Path) ?? publicDirectory, newBaseName + suffix);
            var source = Path.GetFullPath(variant.Path);
            var destinationFullPath = Path.GetFullPath(destination);
            replacements[source] = destinationFullPath;
            moves[source] = destinationFullPath;
        }

        var duplicateDestination = moves
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        if (duplicateDestination is not null)
        {
            return OperationResult<SshKeyRenamePlan>.Fail(
                "Multiple key files would be renamed to the same destination.",
                duplicateDestination.Key);
        }

        foreach (var move in moves)
        {
            if (!PathsEqual(move.Key, move.Value) && _fileSystem.FileExists(move.Value))
            {
                return OperationResult<SshKeyRenamePlan>.Fail(
                    "A rename destination already exists. Choose another filename.",
                    move.Value);
            }
        }

        var affectedIdentities = config.Identities
            .Where(item => replacements.Keys.Any(path =>
                PathsEqual(path, item.PrivateKeyPath) || PathsEqual(path, item.PublicKeyPath)))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var previewIdentities = config.Identities.Select(CloneIdentity).ToList();
        ApplyPathReplacements(previewIdentities, replacements);
        var originalSsh = await _sshConfigService.ReadRawAsync(cancellationToken).ConfigureAwait(false);
        var updatedSsh = originalSsh;
        var affectedIds = affectedIdentities.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var updatedIdentity in previewIdentities.Where(item => affectedIds.Contains(item.Id)))
        {
            var service = config.FindService(updatedIdentity.ServiceInstanceId);
            if (service is null)
            {
                return OperationResult<SshKeyRenamePlan>.Fail(
                    "An affected identity references a Git service that no longer exists.",
                    $"Identity '{updatedIdentity.DisplayName}' references service '{updatedIdentity.ServiceInstanceId}'.");
            }

            updatedSsh = _sshConfigService.PreviewUpsert(updatedSsh, service, updatedIdentity).UpdatedText;
        }

        return OperationResult<SshKeyRenamePlan>.Ok(new SshKeyRenamePlan
        {
            IdentityId = identity.Id,
            IdentityDisplayName = identity.DisplayName,
            NewBaseName = newBaseName,
            OriginalPrivateKeyPath = originalPrivatePath,
            OriginalPublicKeyPath = originalPublicPath,
            NewPrivateKeyPath = Path.GetFullPath(newPrivatePath),
            NewPublicKeyPath = Path.GetFullPath(newPublicPath),
            FileMoves = moves
                .Where(item => !PathsEqual(item.Key, item.Value))
                .Select(item => new SshKeyFileMove { SourcePath = item.Key, DestinationPath = item.Value })
                .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PathReplacements = replacements
                .Where(item => !PathsEqual(item.Key, item.Value))
                .Select(item => new SshKeyPathReplacement { SourcePath = item.Key, DestinationPath = item.Value })
                .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AffectedIdentityIds = affectedIdentities.Select(item => item.Id).ToList(),
            AffectedIdentityNames = affectedIdentities.Select(item => item.DisplayName).ToList(),
            SshConfigDiff = TextDiffService.CreateSimpleDiff(originalSsh, updatedSsh, "ssh_config.before", "ssh_config.after")
        }, "SSH key rename plan created.");
    }

    public async Task<OperationResult<SshKeyRenameResult>> ApplyAsync(
        SshKeyRenamePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var currentPlanResult = await BuildPlanAsync(plan.IdentityId, plan.NewBaseName, cancellationToken).ConfigureAwait(false);
        if (!currentPlanResult.Success || currentPlanResult.Value is null)
        {
            return OperationResult<SshKeyRenameResult>.Fail(currentPlanResult.Message, currentPlanResult.Errors.ToArray());
        }

        var currentPlan = currentPlanResult.Value;
        if (!PathsEqual(currentPlan.OriginalPrivateKeyPath, plan.OriginalPrivateKeyPath)
            || !PathsEqual(currentPlan.OriginalPublicKeyPath, plan.OriginalPublicKeyPath))
        {
            return OperationResult<SshKeyRenameResult>.Fail(
                "The identity changed after the rename preview was created. Refresh and preview the operation again.");
        }

        var configSnapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = configSnapshot.Config;
        var originalPaths = config.Identities.ToDictionary(
            item => item.Id,
            item => (item.PrivateKeyPath, item.PublicKeyPath),
            StringComparer.OrdinalIgnoreCase);
        var originalSsh = await _sshConfigService.ReadRawAsync(cancellationToken).ConfigureAwait(false);
        var replacements = currentPlan.PathReplacements.ToDictionary(
            item => Path.GetFullPath(item.SourcePath),
            item => Path.GetFullPath(item.DestinationPath),
            StringComparer.OrdinalIgnoreCase);
        var moved = new List<SshKeyFileMove>();
        var backups = new List<string>();
        AppConfigFileVersion? savedConfigVersion = null;

        try
        {
            await _backupService.CreateSnapshotAsync(
                $"Rename SSH key files: {currentPlan.IdentityDisplayName}",
                cancellationToken).ConfigureAwait(false);

            foreach (var move in currentPlan.FileMoves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_fileSystem.FileExists(move.SourcePath))
                {
                    throw new FileNotFoundException("A key file disappeared after preview.", move.SourcePath);
                }

                var backupPath = $"{move.SourcePath}.gitkeyrouter.{_clock.LocalNow:yyyyMMdd-HHmmss}.{Guid.NewGuid():N}.bak";
                _fileSystem.CopyFile(move.SourcePath, backupPath, false);
                backups.Add(backupPath);
                _fileSystem.MoveFile(move.SourcePath, move.DestinationPath, false);
                moved.Add(move);
            }

            ApplyPathReplacements(config.Identities, replacements);
            savedConfigVersion = await _configStore.SaveIfUnchangedAsync(
                config,
                configSnapshot.Version,
                cancellationToken).ConfigureAwait(false);

            var updatedSsh = originalSsh;
            var affectedIds = currentPlan.AffectedIdentityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in config.Identities.Where(item => affectedIds.Contains(item.Id)))
            {
                var service = config.FindService(identity.ServiceInstanceId)
                    ?? throw new InvalidOperationException(
                        $"Identity '{identity.DisplayName}' references missing Git service '{identity.ServiceInstanceId}'.");
                updatedSsh = _sshConfigService.PreviewUpsert(updatedSsh, service, identity).UpdatedText;
            }

            var sshPreview = _sshConfigService.CreatePreview(
                $"Update SSH key paths after renaming {currentPlan.IdentityDisplayName}",
                originalSsh,
                updatedSsh);
            var sshResult = await _sshConfigService.ApplyAsync(
                sshPreview,
                $"Update SSH key paths after renaming: {currentPlan.IdentityDisplayName}",
                cancellationToken).ConfigureAwait(false);
            if (!sshResult.Success)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, new[] { sshResult.Message }.Concat(sshResult.Errors)));
            }

            return OperationResult<SshKeyRenameResult>.Ok(new SshKeyRenameResult
            {
                Plan = currentPlan,
                BackupFiles = backups
            }, "SSH key files and related configuration were updated.");
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<string>();
            try
            {
                if (savedConfigVersion is not null)
                {
                    var rollbackSnapshot = await _configStore.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                    foreach (var identity in rollbackSnapshot.Config.Identities)
                    {
                        if (originalPaths.TryGetValue(identity.Id, out var paths))
                        {
                            identity.PrivateKeyPath = paths.PrivateKeyPath;
                            identity.PublicKeyPath = paths.PublicKeyPath;
                        }
                    }

                    await _configStore.SaveIfUnchangedAsync(
                        rollbackSnapshot.Config,
                        savedConfigVersion,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackException)
            {
                rollbackErrors.Add($"Configuration rollback failed: {rollbackException.Message}");
            }

            foreach (var move in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (_fileSystem.FileExists(move.DestinationPath) && !_fileSystem.FileExists(move.SourcePath))
                    {
                        _fileSystem.MoveFile(move.DestinationPath, move.SourcePath, false);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add($"File rollback failed for '{move.SourcePath}': {rollbackException.Message}");
                }
            }

            try
            {
                var currentSsh = await _sshConfigService.ReadRawAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(currentSsh, originalSsh, StringComparison.Ordinal))
                {
                    var restorePreview = _sshConfigService.CreatePreview(
                        "Rollback SSH config after key rename failure",
                        currentSsh,
                        originalSsh);
                    var restoreResult = await _sshConfigService.ApplyAsync(
                        restorePreview,
                        "Rollback SSH config after key rename failure",
                        CancellationToken.None).ConfigureAwait(false);
                    if (!restoreResult.Success)
                    {
                        rollbackErrors.Add($"SSH config rollback failed: {restoreResult.Message}");
                    }
                }
            }
            catch (Exception rollbackException)
            {
                rollbackErrors.Add($"SSH config rollback failed: {rollbackException.Message}");
            }

            return OperationResult<SshKeyRenameResult>.Fail(
                "Unable to rename the SSH key files. Rollback was attempted.",
                new[] { exception.Message }.Concat(rollbackErrors).ToArray());
        }
    }

    private static void ApplyPathReplacements(
        IEnumerable<GitIdentity> identities,
        IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var identity in identities)
        {
            var privatePath = Path.GetFullPath(identity.PrivateKeyPath);
            var publicPath = Path.GetFullPath(identity.PublicKeyPath);
            if (replacements.TryGetValue(privatePath, out var newPrivatePath))
            {
                identity.PrivateKeyPath = newPrivatePath;
            }

            if (replacements.TryGetValue(publicPath, out var newPublicPath))
            {
                identity.PublicKeyPath = newPublicPath;
            }
        }
    }

    private static GitIdentity CloneIdentity(GitIdentity identity)
        => new()
        {
            Id = identity.Id,
            ServiceInstanceId = identity.ServiceInstanceId,
            DisplayName = identity.DisplayName,
            AccountName = identity.AccountName,
            HostAlias = identity.HostAlias,
            PrivateKeyPath = identity.PrivateKeyPath,
            PublicKeyPath = identity.PublicKeyPath,
            EmailOrComment = identity.EmailOrComment,
            CreatedAt = identity.CreatedAt
        };

    private static string? ValidateBaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "A filename is required.";
        }

        if (!string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "Enter a filename only, without a directory or invalid filename characters.";
        }

        if (value.StartsWith(".gitkeyrouter", StringComparison.OrdinalIgnoreCase))
        {
            return "The .gitkeyrouter prefix is reserved for backups and temporary files.";
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
