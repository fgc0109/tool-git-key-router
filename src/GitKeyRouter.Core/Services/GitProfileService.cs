using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed class GitProfileService
{
    private const string MasterFileName = "profiles.gitconfig";
    private const string TransactionManifestFileName = "transaction.json";
    private const string TransactionStatePrepared = "prepared";
    private const string TransactionStateApplying = "applying";
    private const string TransactionStateCommitted = "committed";
    private const string TransactionStateRolledBack = "rolled-back";
    private static readonly JsonSerializerOptions TransactionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly IAppConfigStore _configStore;
    private readonly IBackupService _backupService;
    private readonly IFileSystem _fileSystem;
    private readonly IAppPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly IToolchainService _toolchainService;

    public GitProfileService(
        IAppConfigStore configStore,
        IBackupService backupService,
        IFileSystem fileSystem,
        IAppPaths paths,
        IProcessRunner processRunner,
        IToolchainService toolchainService)
    {
        _configStore = configStore;
        _backupService = backupService;
        _fileSystem = fileSystem;
        _paths = paths;
        _processRunner = processRunner;
        _toolchainService = toolchainService;
    }

    public string ProfilesDirectory => Path.Combine(_paths.AppDataDirectory, "git-profiles");

    public string MasterConfigPath => Path.Combine(ProfilesDirectory, MasterFileName);

    public string TransactionRootDirectory => Path.Combine(_paths.AppDataDirectory, "git-profile-transactions");

    public async Task<OperationResult> RecoverInterruptedTransactionsAsync(
        CancellationToken cancellationToken = default)
    {
        var directories = _fileSystem.EnumerateDirectories(TransactionRootDirectory)
            .Where(path => Path.GetFileName(path).StartsWith("transaction-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (directories.Count == 0)
        {
            return OperationResult.Ok("No interrupted Git Profile transaction was found.");
        }

        ToolchainInfo? tools = null;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, TransactionManifestFileName);
            if (!_fileSystem.FileExists(manifestPath))
            {
                TryDeleteDirectory(directory);
                continue;
            }

            GitProfileTransactionManifest manifest;
            try
            {
                manifest = await ReadTransactionAsync(directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return OperationResult.Fail(
                    "An interrupted Git Profile transaction could not be validated. No recovery changes were made.",
                    exception.Message,
                    directory);
            }

            if (manifest.State is TransactionStateCommitted or TransactionStateRolledBack or TransactionStatePrepared)
            {
                TryDeleteDirectory(directory);
                continue;
            }

            if (!string.Equals(manifest.State, TransactionStateApplying, StringComparison.Ordinal))
            {
                return OperationResult.Fail(
                    "An interrupted Git Profile transaction has an unknown state. No recovery changes were made.",
                    $"State: {manifest.State}",
                    directory);
            }

            tools ??= await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (!tools.Git.Exists || string.IsNullOrWhiteSpace(tools.Git.SelectedPath))
            {
                return OperationResult.Fail(
                    "An interrupted Git Profile transaction requires recovery, but git.exe was not found.",
                    directory);
            }

            var snapshot = new GitProfileSnapshot(
                manifest.Files.ToDictionary(
                    item => item.Path,
                    item => new GitProfileFileSnapshot(item.Exists, item.Content),
                    StringComparer.OrdinalIgnoreCase),
                manifest.IncludePaths);
            var rollbackErrors = await RollbackAsync(snapshot, tools.Git.SelectedPath).ConfigureAwait(false);
            if (rollbackErrors.Count > 0)
            {
                return OperationResult.Fail(
                    "Interrupted Git Profile transaction recovery failed.",
                    rollbackErrors.ToArray());
            }

            try
            {
                manifest.State = TransactionStateRolledBack;
                await WriteTransactionAsync(directory, manifest, CancellationToken.None).ConfigureAwait(false);
                TryDeleteDirectory(directory);
            }
            catch (Exception exception)
            {
                return OperationResult.Fail(
                    "The Git Profile files were recovered, but the transaction journal could not be finalized.",
                    exception.Message,
                    directory);
            }
        }

        return OperationResult.Ok("Interrupted Git Profile transactions were recovered.");
    }

    public async Task<OperationResult<GitProfile>> SaveProfileAsync(
        GitProfile profile,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var validation = GitProfileValidator.Validate(profile, config);
        if (!validation.IsValid)
        {
            return OperationResult<GitProfile>.Fail("Git Profile validation failed.", validation.Errors.ToArray());
        }

        profile.DisplayName = profile.DisplayName.Trim();
        profile.UserName = profile.UserName.Trim();
        profile.UserEmail = profile.UserEmail.Trim();
        profile.SigningKey = profile.SigningKey.Trim();
        await _backupService.CreateSnapshotAsync($"Save Git Profile: {profile.DisplayName}", cancellationToken).ConfigureAwait(false);
        var index = config.GitProfiles.FindIndex(item => string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            config.GitProfiles[index] = profile;
        }
        else
        {
            config.GitProfiles.Add(profile);
        }

        await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        return OperationResult<GitProfile>.Ok(profile, "Git Profile saved.");
    }

    public async Task<OperationResult> DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = config.GitProfiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return OperationResult.Fail("Git Profile was not found.");
        }

        await _backupService.CreateSnapshotAsync($"Delete Git Profile: {profile.DisplayName}", cancellationToken).ConfigureAwait(false);
        config.GitProfiles.Remove(profile);
        config.GitProfileRules.RemoveAll(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Git Profile deleted.");
    }

    public async Task<OperationResult<GitProfileRule>> SaveRuleAsync(
        GitProfileRule rule,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var validation = GitProfileRuleValidator.Validate(rule, config);
        if (!validation.IsValid)
        {
            return OperationResult<GitProfileRule>.Fail("Git Profile rule validation failed.", validation.Errors.ToArray());
        }

        rule.Pattern = GitProfileRuleValidator.NormalizePattern(rule);
        await _backupService.CreateSnapshotAsync("Save Git Profile rule", cancellationToken).ConfigureAwait(false);
        var index = config.GitProfileRules.FindIndex(item => string.Equals(item.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            config.GitProfileRules[index] = rule;
        }
        else
        {
            config.GitProfileRules.Add(rule);
        }

        await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        return OperationResult<GitProfileRule>.Ok(rule, "Git Profile rule saved.");
    }

    public async Task<OperationResult> DeleteRuleAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var rule = config.GitProfileRules.FirstOrDefault(item => string.Equals(item.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return OperationResult.Fail("Git Profile rule was not found.");
        }

        await _backupService.CreateSnapshotAsync("Delete Git Profile rule", cancellationToken).ConfigureAwait(false);
        config.GitProfileRules.Remove(rule);
        await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Git Profile rule deleted.");
    }

    public async Task<GitProfileConfigPreview> BuildPreviewAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var files = BuildProfileFiles(config);
        var expectedProfilePaths = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var master = BuildMasterConfig(config, files);
        var previewPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MasterConfigPath
        };
        previewPaths.UnionWith(files.Keys);
        previewPaths.UnionWith(_fileSystem.EnumerateFiles(ProfilesDirectory, "profile-*.gitconfig"));

        var originalTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var originalFiles = new Dictionary<string, FileContentSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in previewPaths)
        {
            var exists = _fileSystem.FileExists(path);
            var text = exists
                ? await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            originalTexts[path] = text;
            originalFiles[path] = FileContentSnapshot.Create(exists, text);
        }

        var existingMaster = originalTexts[MasterConfigPath];
        var diff = new StringBuilder(TextDiffService.CreateSimpleDiff(
            existingMaster,
            master,
            MasterFileName + ".before",
            MasterFileName + ".after"));
        var hasChanges = !string.Equals(existingMaster, master, StringComparison.Ordinal);
        foreach (var (path, text) in files)
        {
            var original = originalTexts[path];
            if (!string.Equals(original, text, StringComparison.Ordinal))
            {
                hasChanges = true;
                diff.AppendLine().Append(TextDiffService.CreateSimpleDiff(
                    original,
                    text,
                    Path.GetFileName(path) + ".before",
                    Path.GetFileName(path) + ".after"));
            }
        }

        foreach (var path in previewPaths.Where(path =>
                     !string.Equals(path, MasterConfigPath, StringComparison.OrdinalIgnoreCase)
                     && !expectedProfilePaths.Contains(path)))
        {
            hasChanges = true;
            diff.AppendLine().Append(TextDiffService.CreateSimpleDiff(
                originalTexts[path],
                string.Empty,
                Path.GetFileName(path) + ".before",
                Path.GetFileName(path) + ".after"));
        }

        return new GitProfileConfigPreview
        {
            MasterConfigPath = MasterConfigPath,
            MasterConfigText = master,
            OriginalFiles = originalFiles,
            ProfileFiles = files,
            DiffText = diff.ToString(),
            HasChanges = hasChanges
        };
    }

    public async Task<OperationResult<GitProfileApplyResult>> ApplyAsync(
        GitProfileConfigPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var recovery = await RecoverInterruptedTransactionsAsync(cancellationToken).ConfigureAwait(false);
        if (!recovery.Success)
        {
            return OperationResult<GitProfileApplyResult>.Fail(recovery.Message, recovery.Errors.ToArray());
        }

        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.Git.Exists || string.IsNullOrWhiteSpace(tools.Git.SelectedPath))
        {
            return OperationResult<GitProfileApplyResult>.Fail(
                "git.exe was not found. No Git Profile files were changed.");
        }

        var gitPath = tools.Git.SelectedPath;
        var includeRead = await ReadGlobalIncludesAsync(gitPath, cancellationToken).ConfigureAwait(false);
        if (!includeRead.Success || includeRead.Value is null)
        {
            return OperationResult<GitProfileApplyResult>.Fail(
                includeRead.Message,
                includeRead.Errors.ToArray());
        }

        var changedPreviewPath = await FindChangedPreviewPathAsync(preview, cancellationToken).ConfigureAwait(false);
        if (changedPreviewPath is not null)
        {
            return StalePreviewFailure(changedPreviewPath);
        }

        var originalIncludes = includeRead.Value;
        var snapshot = await CaptureSnapshotAsync(preview, originalIncludes, cancellationToken).ConfigureAwait(false);
        var stagingDirectory = Path.Combine(ProfilesDirectory, $".pending-{Guid.NewGuid():N}");
        var stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mutationStarted = false;
        ProcessResult? registration = null;
        GitProfileTransactionManifest? transaction = null;
        string? transactionDirectory = null;

        try
        {
            (transactionDirectory, transaction) = await CreateTransactionAsync(
                snapshot,
                gitPath,
                cancellationToken).ConfigureAwait(false);
            await _backupService.CreateSnapshotAsync(
                "Apply Git Profile conditional config",
                cancellationToken).ConfigureAwait(false);

            _fileSystem.CreateDirectory(stagingDirectory);
            stagedFiles[MasterConfigPath] = Path.Combine(stagingDirectory, MasterFileName);
            foreach (var path in preview.ProfileFiles.Keys)
            {
                stagedFiles[path] = Path.Combine(stagingDirectory, Path.GetFileName(path));
            }

            await StageAndValidateAsync(
                gitPath,
                stagedFiles[MasterConfigPath],
                preview.MasterConfigText,
                cancellationToken).ConfigureAwait(false);
            foreach (var (path, text) in preview.ProfileFiles)
            {
                await StageAndValidateAsync(
                    gitPath,
                    stagedFiles[path],
                    text,
                    cancellationToken).ConfigureAwait(false);
            }

            changedPreviewPath = await FindChangedPreviewPathAsync(preview, cancellationToken).ConfigureAwait(false);
            if (changedPreviewPath is not null)
            {
                return StalePreviewFailure(changedPreviewPath);
            }

            transaction.State = TransactionStateApplying;
            await WriteTransactionAsync(transactionDirectory, transaction, cancellationToken).ConfigureAwait(false);
            mutationStarted = true;
            _fileSystem.CreateDirectory(ProfilesDirectory);
            await _fileSystem.WriteAllTextAtomicAsync(
                MasterConfigPath,
                preview.MasterConfigText,
                cancellationToken).ConfigureAwait(false);
            foreach (var (path, text) in preview.ProfileFiles)
            {
                await _fileSystem.WriteAllTextAtomicAsync(path, text, cancellationToken).ConfigureAwait(false);
            }

            var expectedPaths = preview.ProfileFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _fileSystem.EnumerateFiles(ProfilesDirectory, "profile-*.gitconfig"))
            {
                if (!expectedPaths.Contains(path))
                {
                    _fileSystem.DeleteFile(path);
                }
            }

            var includePath = ToGitPath(MasterConfigPath);
            var registered = originalIncludes.Any(item =>
                string.Equals(
                    NormalizeGitPath(item),
                    NormalizeGitPath(includePath),
                    StringComparison.OrdinalIgnoreCase));
            if (!registered)
            {
                registration = await _processRunner.RunAsync(new ProcessRequest
                {
                    ExecutablePath = gitPath,
                    Arguments = ["config", "--global", "--add", "include.path", includePath]
                }, cancellationToken).ConfigureAwait(false);
                if (!registration.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to register the Git Profile include file. {DescribeProcessFailure(registration)}");
                }
            }

            var expectedIncludes = originalIncludes.ToList();
            if (!registered)
            {
                expectedIncludes.Add(includePath);
            }

            await VerifyAppliedStateAsync(preview, gitPath, expectedIncludes, cancellationToken).ConfigureAwait(false);

            transaction.State = TransactionStateCommitted;
            await WriteTransactionAsync(transactionDirectory, transaction, cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(transactionDirectory);

            return OperationResult<GitProfileApplyResult>.Ok(new GitProfileApplyResult
            {
                MasterConfigPath = MasterConfigPath,
                ProfileFileCount = preview.ProfileFiles.Count,
                IncludeRegistrationResult = registration
            }, "Git Profile conditional config applied transactionally.");
        }
        catch (Exception exception)
        {
            if (!mutationStarted)
            {
                var journalError = await TryFinalizeTransactionAsync(
                    transactionDirectory,
                    transaction,
                    TransactionStateRolledBack).ConfigureAwait(false);
                return OperationResult<GitProfileApplyResult>.Fail(
                    "Git Profile files were not changed because preparation failed.",
                    new[] { exception.Message }.Concat(journalError is null ? [] : [journalError]).ToArray());
            }

            var rollbackErrors = await RollbackAsync(snapshot, gitPath).ConfigureAwait(false);
            if (rollbackErrors.Count == 0)
            {
                var finalizationError = await TryFinalizeTransactionAsync(
                    transactionDirectory,
                    transaction,
                    TransactionStateRolledBack).ConfigureAwait(false);
                if (finalizationError is not null)
                {
                    rollbackErrors.Add(finalizationError);
                }
            }

            if (rollbackErrors.Count == 0)
            {
                return OperationResult<GitProfileApplyResult>.Fail(
                    "Git Profile application failed. The original files and global include.path values were restored automatically.",
                    exception.Message);
            }

            return OperationResult<GitProfileApplyResult>.Fail(
                "Git Profile application failed, and the automatic rollback also failed.",
                [exception.Message, .. rollbackErrors]);
        }
        finally
        {
            try
            {
                _fileSystem.DeleteDirectory(stagingDirectory, true);
            }
            catch
            {
                // A stale staging directory is non-authoritative and must not mask the transaction result.
            }
        }
    }

    private async Task<(string Directory, GitProfileTransactionManifest Manifest)> CreateTransactionAsync(
        GitProfileSnapshot snapshot,
        string gitExecutablePath,
        CancellationToken cancellationToken)
    {
        _fileSystem.CreateDirectory(TransactionRootDirectory);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(TransactionRootDirectory, $"transaction-{id}");
        _fileSystem.CreateDirectory(directory);
        var manifest = new GitProfileTransactionManifest
        {
            Id = id,
            State = TransactionStatePrepared,
            GitExecutablePath = gitExecutablePath,
            IncludePaths = snapshot.IncludePaths.ToList(),
            Files = snapshot.Files
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new GitProfileTransactionFile
                {
                    Path = Path.GetFullPath(item.Key),
                    Exists = item.Value.Exists,
                    Content = item.Value.Content,
                    Sha256 = ComputeSha256(item.Value.Content ?? string.Empty)
                })
                .ToList()
        };

        await WriteTransactionAsync(directory, manifest, cancellationToken).ConfigureAwait(false);
        _ = await ReadTransactionAsync(directory, cancellationToken).ConfigureAwait(false);
        return (directory, manifest);
    }

    private async Task<GitProfileTransactionManifest> ReadTransactionAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, TransactionManifestFileName);
        var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<GitProfileTransactionManifest>(text, TransactionJsonOptions)
            ?? throw new InvalidDataException("The Git Profile transaction journal is empty.");
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidDataException("The Git Profile transaction journal version or identity is invalid.");
        }

        manifest.Files ??= [];
        manifest.IncludePaths ??= [];
        var expectedPrefix = Path.GetFullPath(ProfilesDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var fullPath = Path.GetFullPath(file.Path);
            if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Transaction file path escapes the Git Profile directory: {file.Path}");
            }

            if (file.Exists && file.Content is null)
            {
                throw new InvalidDataException($"Transaction snapshot content is missing: {file.Path}");
            }

            if (!string.Equals(file.Sha256, ComputeSha256(file.Content ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Transaction snapshot SHA-256 is invalid: {file.Path}");
            }

            if (!paths.Add(fullPath))
            {
                throw new InvalidDataException($"Transaction snapshot contains a duplicate file path: {file.Path}");
            }

            file.Path = fullPath;
        }

        return manifest;
    }

    private Task WriteTransactionAsync(
        string directory,
        GitProfileTransactionManifest manifest,
        CancellationToken cancellationToken)
        => _fileSystem.WriteAllTextAtomicAsync(
            Path.Combine(directory, TransactionManifestFileName),
            JsonSerializer.Serialize(manifest, TransactionJsonOptions) + Environment.NewLine,
            cancellationToken);

    private async Task<string?> TryFinalizeTransactionAsync(
        string? directory,
        GitProfileTransactionManifest? manifest,
        string state)
    {
        if (directory is null || manifest is null)
        {
            return null;
        }

        try
        {
            manifest.State = state;
            await WriteTransactionAsync(directory, manifest, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(directory);
            return null;
        }
        catch (Exception exception)
        {
            return $"Transaction journal finalization failed: {exception.Message}";
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            _fileSystem.DeleteDirectory(path, true);
        }
        catch
        {
            // A committed or rolled-back journal is safe to retry during the next startup.
        }
    }

    private static string ComputeSha256(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private async Task<OperationResult<IReadOnlyList<string>>> ReadGlobalIncludesAsync(
        string gitPath,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = gitPath,
            Arguments = ["config", "--global", "--get-all", "include.path"]
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && result.ExitCode != 1)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(
                "Unable to read the global Git include.path configuration. No Git Profile files were changed.",
                DescribeProcessFailure(result));
        }

        return OperationResult<IReadOnlyList<string>>.Ok(ParseLines(result.StandardOutput));
    }

    private async Task<string?> FindChangedPreviewPathAsync(
        GitProfileConfigPreview preview,
        CancellationToken cancellationToken)
    {
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MasterConfigPath
        };
        currentPaths.UnionWith(preview.ProfileFiles.Keys);
        currentPaths.UnionWith(_fileSystem.EnumerateFiles(ProfilesDirectory, "profile-*.gitconfig"));

        var previewPaths = preview.OriginalFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!currentPaths.SetEquals(previewPaths))
        {
            return currentPaths.Except(previewPaths, StringComparer.OrdinalIgnoreCase)
                .Concat(previewPaths.Except(currentPaths, StringComparer.OrdinalIgnoreCase))
                .FirstOrDefault() ?? ProfilesDirectory;
        }

        foreach (var path in previewPaths)
        {
            var exists = _fileSystem.FileExists(path);
            var text = exists
                ? await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            if (!preview.OriginalFiles[path].Matches(exists, text))
            {
                return path;
            }
        }

        return null;
    }

    private static OperationResult<GitProfileApplyResult> StalePreviewFailure(string path)
        => OperationResult<GitProfileApplyResult>.Fail(
            "文件在预览后已发生变化，请重新生成预览。",
            $"Git Profile conflict: {path}");

    private async Task<GitProfileSnapshot> CaptureSnapshotAsync(
        GitProfileConfigPreview preview,
        IReadOnlyList<string> includePaths,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MasterConfigPath
        };
        paths.UnionWith(preview.ProfileFiles.Keys);
        paths.UnionWith(_fileSystem.EnumerateFiles(ProfilesDirectory, "profile-*.gitconfig"));

        var files = new Dictionary<string, GitProfileFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var exists = _fileSystem.FileExists(path);
            files[path] = new GitProfileFileSnapshot(
                exists,
                exists
                    ? await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                    : null);
        }

        return new GitProfileSnapshot(files, includePaths.ToList());
    }

    private async Task StageAndValidateAsync(
        string gitPath,
        string stagingPath,
        string content,
        CancellationToken cancellationToken)
    {
        await _fileSystem.WriteAllTextAtomicAsync(stagingPath, content, cancellationToken).ConfigureAwait(false);
        var staged = await _fileSystem.ReadAllTextAsync(stagingPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(staged, content, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Staged Git Profile file verification failed: {Path.GetFileName(stagingPath)}");
        }

        var validation = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = gitPath,
            Arguments = ["config", "--file", ToGitPath(stagingPath), "--list"]
        }, cancellationToken).ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(
                $"Generated Git Profile file is invalid: {Path.GetFileName(stagingPath)}. {DescribeProcessFailure(validation)}");
        }
    }

    private async Task VerifyAppliedStateAsync(
        GitProfileConfigPreview preview,
        string gitPath,
        IReadOnlyList<string> expectedIncludes,
        CancellationToken cancellationToken)
    {
        await VerifyFileAsync(MasterConfigPath, preview.MasterConfigText, cancellationToken).ConfigureAwait(false);
        foreach (var (path, text) in preview.ProfileFiles)
        {
            await VerifyFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        }

        var expectedPaths = preview.ProfileFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = _fileSystem.EnumerateFiles(ProfilesDirectory, "profile-*.gitconfig")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPaths.SetEquals(expectedPaths))
        {
            throw new InvalidOperationException("Git Profile file-set verification failed after applying the transaction.");
        }

        var includeRead = await ReadGlobalIncludesAsync(gitPath, cancellationToken).ConfigureAwait(false);
        if (!includeRead.Success || includeRead.Value is null
            || !NormalizedPathsEqual(includeRead.Value, expectedIncludes))
        {
            throw new InvalidOperationException(
                "Global Git include.path verification failed after applying the transaction.");
        }
    }

    private async Task VerifyFileAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(path))
        {
            throw new InvalidOperationException($"Expected Git Profile file was not created: {path}");
        }

        var actual = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Git Profile file verification failed: {path}");
        }
    }

    private async Task<List<string>> RollbackAsync(GitProfileSnapshot snapshot, string gitPath)
    {
        var errors = new List<string>();
        foreach (var (path, file) in snapshot.Files)
        {
            try
            {
                if (file.Exists)
                {
                    await _fileSystem.WriteAllTextAtomicAsync(
                        path,
                        file.Content ?? string.Empty,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    _fileSystem.DeleteFile(path);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"File rollback failed for '{path}': {exception.Message}");
            }
        }

        try
        {
            var unset = await _processRunner.RunAsync(new ProcessRequest
            {
                ExecutablePath = gitPath,
                Arguments = ["config", "--global", "--unset-all", "include.path"]
            }, CancellationToken.None).ConfigureAwait(false);
            if (!unset.Succeeded && unset.ExitCode is not (1 or 5))
            {
                errors.Add($"include.path rollback reset failed: {DescribeProcessFailure(unset)}");
            }

            foreach (var includePath in snapshot.IncludePaths)
            {
                var add = await _processRunner.RunAsync(new ProcessRequest
                {
                    ExecutablePath = gitPath,
                    Arguments = ["config", "--global", "--add", "include.path", includePath]
                }, CancellationToken.None).ConfigureAwait(false);
                if (!add.Succeeded)
                {
                    errors.Add($"include.path rollback add failed for '{includePath}': {DescribeProcessFailure(add)}");
                }
            }

            var restored = await ReadGlobalIncludesAsync(gitPath, CancellationToken.None).ConfigureAwait(false);
            if (!restored.Success || restored.Value is null
                || !NormalizedPathsEqual(restored.Value, snapshot.IncludePaths))
            {
                errors.Add("include.path rollback verification failed.");
            }
        }
        catch (Exception exception)
        {
            errors.Add($"include.path rollback failed: {exception.Message}");
        }

        return errors;
    }

    private static IReadOnlyList<string> ParseLines(string text)
        => text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static bool NormalizedPathsEqual(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected)
        => actual.Select(NormalizeGitPath)
            .SequenceEqual(expected.Select(NormalizeGitPath), StringComparer.OrdinalIgnoreCase);

    private static string DescribeProcessFailure(ProcessResult result)
        => !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : result.StartException?.Message
                ?? (result.TimedOut
                    ? "The Git command timed out."
                    : result.Cancelled
                        ? "The Git command was cancelled."
                        : $"Git exited with code {result.ExitCode?.ToString() ?? "unknown"}.");

    private sealed record GitProfileFileSnapshot(bool Exists, string? Content);

    private sealed record GitProfileSnapshot(
        IReadOnlyDictionary<string, GitProfileFileSnapshot> Files,
        IReadOnlyList<string> IncludePaths);

    private sealed class GitProfileTransactionManifest
    {
        public int SchemaVersion { get; set; } = 1;

        public string Id { get; set; } = string.Empty;

        public string State { get; set; } = TransactionStatePrepared;

        public string GitExecutablePath { get; set; } = string.Empty;

        public List<GitProfileTransactionFile> Files { get; set; } = [];

        public List<string> IncludePaths { get; set; } = [];
    }

    private sealed class GitProfileTransactionFile
    {
        public string Path { get; set; } = string.Empty;

        public bool Exists { get; set; }

        public string? Content { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }

    public GitProfile? ResolveProfile(AppConfig config, string? repositoryDirectory, IEnumerable<string>? remoteUrls = null)
    {
        var enabled = config.GitProfileRules.Where(item => item.Enabled).ToList();
        if (!string.IsNullOrWhiteSpace(repositoryDirectory))
        {
            var directory = GitProfileRuleValidator.NormalizeDirectoryPattern(repositoryDirectory);
            var match = enabled.Where(item => item.Kind == GitProfileRuleKind.Directory
                    && directory.StartsWith(GitProfileRuleValidator.NormalizeDirectoryPattern(item.Pattern), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => GitProfileRuleValidator.NormalizeDirectoryPattern(item.Pattern).Length)
                .FirstOrDefault();
            if (match is not null)
            {
                return config.GitProfiles.FirstOrDefault(item => string.Equals(item.Id, match.ProfileId, StringComparison.OrdinalIgnoreCase));
            }
        }

        foreach (var remoteUrl in remoteUrls ?? [])
        {
            var match = enabled.FirstOrDefault(item => item.Kind == GitProfileRuleKind.RemoteUrl
                && MatchesRemotePattern(remoteUrl, item.Pattern));
            if (match is not null)
            {
                return config.GitProfiles.FirstOrDefault(item => string.Equals(item.Id, match.ProfileId, StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    private IReadOnlyDictionary<string, string> BuildProfileFiles(AppConfig config)
        => config.GitProfiles.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(
                profile => ProfilePath(profile.Id),
                profile => BuildProfileConfig(profile, config),
                StringComparer.OrdinalIgnoreCase);

    private string BuildMasterConfig(AppConfig config, IReadOnlyDictionary<string, string> files)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# GitKeyRouter managed Git Profile conditions. Do not edit manually.");
        foreach (var rule in config.GitProfileRules.Where(item => item.Enabled)
                     .OrderBy(item => item.Kind)
                     .ThenBy(item => item.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            if (!files.TryGetValue(ProfilePath(rule.ProfileId), out _))
            {
                continue;
            }

            var condition = rule.Kind == GitProfileRuleKind.Directory
                ? "gitdir/i:" + GitProfileRuleValidator.NormalizeDirectoryPattern(rule.Pattern)
                : "hasconfig:remote.*.url:" + rule.Pattern.Trim();
            builder.Append("[includeIf \"").Append(Escape(condition)).AppendLine("\"]");
            builder.Append("    path = \"").Append(Escape(ToGitPath(ProfilePath(rule.ProfileId)))).AppendLine("\"");
        }

        return builder.ToString();
    }

    private static string BuildProfileConfig(GitProfile profile, AppConfig config)
    {
        var service = config.FindService(profile.DefaultServiceInstanceId);
        var identity = config.Identities.FirstOrDefault(item => string.Equals(item.Id, profile.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
        var builder = new StringBuilder();
        builder.AppendLine("# GitKeyRouter managed Git Profile. Do not edit manually.");
        builder.Append("# Profile: ").AppendLine(profile.DisplayName);
        if (service is not null)
        {
            builder.Append("# Default service: ").AppendLine(service.DisplayName);
        }

        if (identity is not null)
        {
            builder.Append("# Default SSH identity: ").Append(identity.DisplayName).Append(" (").Append(identity.HostAlias).AppendLine(")");
        }

        builder.AppendLine("[user]");
        builder.Append("    name = \"").Append(Escape(profile.UserName)).AppendLine("\"");
        builder.Append("    email = \"").Append(Escape(profile.UserEmail)).AppendLine("\"");
        if (!string.IsNullOrWhiteSpace(profile.SigningKey))
        {
            builder.Append("    signingKey = \"").Append(Escape(profile.SigningKey)).AppendLine("\"");
        }

        if (profile.EnableCommitSigning)
        {
            builder.AppendLine("[commit]");
            builder.AppendLine("    gpgSign = true");
        }

        return builder.ToString();
    }

    private string ProfilePath(string profileId)
        => Path.Combine(ProfilesDirectory, $"profile-{profileId}.gitconfig");

    private static bool MatchesRemotePattern(string value, string pattern)
    {
        var normalizedPattern = pattern.Trim();
        var wildcard = normalizedPattern.IndexOf('*');
        return wildcard < 0
            ? string.Equals(value, normalizedPattern, StringComparison.OrdinalIgnoreCase)
            : value.StartsWith(normalizedPattern[..wildcard], StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string ToGitPath(string path)
        => path.Replace('\\', '/');

    private static string NormalizeGitPath(string value)
        => value.Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
}
