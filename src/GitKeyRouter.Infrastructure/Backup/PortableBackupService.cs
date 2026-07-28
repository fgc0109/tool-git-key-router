using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Infrastructure.Backup;

public sealed class PortableBackupService : IPortableBackupService
{
    private const string EnvelopeFormat = "GitKeyRouter.PortableBackup";
    private const int EnvelopeVersion = 1;
    private const int PayloadVersion = 1;
    private const int KdfIterations = 210_000;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int MinimumPasswordLength = 12;
    private const int MaximumPackageBytes = 64 * 1024 * 1024;
    private const int MaximumKeyBytes = 16 * 1024 * 1024;
    private const int MaximumTotalKeyBytes = 48 * 1024 * 1024;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("GitKeyRouter.PortableBackup/v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IAppConfigStore _configStore;
    private readonly IGitUrlRewriteStore _gitStore;
    private readonly IBackupService _backupService;
    private readonly IGitProfileMaterializer _gitProfiles;
    private readonly IClock _clock;

    public PortableBackupService(
        IAppPaths paths,
        IFileSystem fileSystem,
        IAppConfigStore configStore,
        IGitUrlRewriteStore gitStore,
        IBackupService backupService,
        IGitProfileMaterializer gitProfiles,
        IClock clock)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _configStore = configStore;
        _gitStore = gitStore;
        _backupService = backupService;
        _gitProfiles = gitProfiles;
        _clock = clock;
    }

    public async Task<OperationResult<PortableBackupPreview>> ExportAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return OperationResult<PortableBackupPreview>.Fail("Choose a portable backup destination.");
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
        {
            return OperationResult<PortableBackupPreview>.Fail(
                $"Portable backup passwords must contain at least {MinimumPasswordLength} characters.");
        }

        byte[]? plaintext = null;
        byte[]? encryptionKey = null;
        try
        {
            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            config.Normalize();
            var rewrites = await _gitStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var sshConfig = _fileSystem.FileExists(_paths.SshConfigPath)
                ? await _fileSystem.ReadAllTextAsync(_paths.SshConfigPath, cancellationToken).ConfigureAwait(false)
                : null;
            var keys = await CaptureKeysAsync(config, cancellationToken).ConfigureAwait(false);
            var payload = new PortablePayload
            {
                SchemaVersion = PayloadVersion,
                CreatedAt = _clock.UtcNow,
                AppConfig = config,
                SshConfigText = sshConfig,
                GitUrlRewrites = rewrites
                    .Select(rule => new GitUrlRewriteRule(rule.BaseUrl, rule.InsteadOfUrl))
                    .ToList(),
                Keys = keys
            };
            ValidatePayload(payload);

            plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            if (plaintext.Length > MaximumPackageBytes)
            {
                return OperationResult<PortableBackupPreview>.Fail("The portable backup payload exceeds the 64 MiB safety limit.");
            }

            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            encryptionKey = DeriveKey(password, salt, KdfIterations);
            using (var aes = new AesGcm(encryptionKey, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            }

            var envelope = new PortableEnvelope
            {
                Format = EnvelopeFormat,
                Version = EnvelopeVersion,
                Kdf = "PBKDF2-SHA256",
                Iterations = KdfIterations,
                Cipher = "AES-256-GCM",
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(ciphertext)
            };
            var packageBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (packageBytes.Length > MaximumPackageBytes)
            {
                return OperationResult<PortableBackupPreview>.Fail("The encrypted portable backup exceeds the 64 MiB safety limit.");
            }

            await _fileSystem.WriteAllBytesAtomicAsync(packagePath, packageBytes, cancellationToken).ConfigureAwait(false);
            return OperationResult<PortableBackupPreview>.Ok(
                CreatePreview(payload, packageBytes.LongLength),
                "Portable encrypted backup exported.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or CryptographicException
            or FormatException)
        {
            return OperationResult<PortableBackupPreview>.Fail(
                "Portable backup export failed. No completed package was published.",
                exception.Message);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    public async Task<OperationResult<PortableBackupPreview>> InspectAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken = default)
    {
        var read = await ReadPackageAsync(packagePath, password, cancellationToken).ConfigureAwait(false);
        return read.Success && read.Value is not null
            ? OperationResult<PortableBackupPreview>.Ok(
                CreatePreview(read.Value.Payload, read.Value.PackageBytes),
                "Portable backup password and package integrity were verified.")
            : OperationResult<PortableBackupPreview>.Fail(read.Message, read.Errors.ToArray());
    }

    public async Task<OperationResult<PortableBackupImportResult>> ImportAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken = default)
    {
        var read = await ReadPackageAsync(packagePath, password, cancellationToken).ConfigureAwait(false);
        if (!read.Success || read.Value is null)
        {
            return OperationResult<PortableBackupImportResult>.Fail(read.Message, read.Errors.ToArray());
        }

        ImportPlan plan;
        try
        {
            plan = BuildImportPlan(read.Value.Payload);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException)
        {
            return OperationResult<PortableBackupImportResult>.Fail(
                "The portable backup import plan is invalid. No changes were made.",
                exception.Message);
        }

        CurrentState current;
        try
        {
            current = await CaptureCurrentStateAsync(plan, cancellationToken).ConfigureAwait(false);
            await _backupService.CreateSnapshotAsync(
                "Before importing portable encrypted backup",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            plan.Dispose();
            return OperationResult<PortableBackupImportResult>.Fail(
                "Could not capture a complete safety state before portable import. No changes were made.",
                exception.Message);
        }

        var mutationStarted = false;
        try
        {
            foreach (var key in plan.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mutationStarted = true;
                await _fileSystem.WriteAllBytesAtomicAsync(key.TargetPath, key.Bytes, cancellationToken).ConfigureAwait(false);
            }

            mutationStarted = true;
            await _configStore.SaveAsync(plan.Config, cancellationToken).ConfigureAwait(false);
            if (plan.SshConfigText is null)
            {
                _fileSystem.DeleteFile(_paths.SshConfigPath);
            }
            else
            {
                await _fileSystem.WriteAllTextAtomicAsync(
                    _paths.SshConfigPath,
                    plan.SshConfigText,
                    cancellationToken).ConfigureAwait(false);
            }

            var rewriteResult = await ReplaceGitRewritesAsync(plan.GitUrlRewrites, cancellationToken).ConfigureAwait(false);
            if (!rewriteResult.Success)
            {
                throw new InvalidOperationException(
                    string.Join(Environment.NewLine, [rewriteResult.Message, .. rewriteResult.Errors]));
            }

            var profileResult = await _gitProfiles.ApplyCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (!profileResult.Success)
            {
                throw new InvalidOperationException(
                    string.Join(Environment.NewLine, [profileResult.Message, .. profileResult.Errors]));
            }

            return OperationResult<PortableBackupImportResult>.Ok(
                new PortableBackupImportResult
                {
                    ManagedKeyDirectory = plan.ManagedKeyDirectory,
                    IdentityCount = plan.Config.Identities.Count,
                    KeyFileCount = plan.Keys.Count,
                    GitRewriteCount = plan.GitUrlRewrites.Count
                },
                "Portable backup imported. Settings, keys, SSH config, Git rewrites, and Git Profile files were restored.");
        }
        catch (OperationCanceledException exception)
        {
            if (!mutationStarted)
            {
                throw;
            }

            var rollbackErrors = await RollbackAsync(current, CancellationToken.None).ConfigureAwait(false);
            return OperationResult<PortableBackupImportResult>.Fail(
                rollbackErrors.Count == 0
                    ? "Portable backup import was cancelled. The original state was restored automatically."
                    : "Portable backup import was cancelled, and automatic rollback reported errors.",
                [exception.Message, .. rollbackErrors]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or JsonException
            or CryptographicException)
        {
            var rollbackErrors = mutationStarted
                ? await RollbackAsync(current, CancellationToken.None).ConfigureAwait(false)
                : [];
            return OperationResult<PortableBackupImportResult>.Fail(
                rollbackErrors.Count == 0
                    ? "Portable backup import failed. The original state was restored automatically."
                    : "Portable backup import failed, and automatic rollback also reported errors.",
                [exception.Message, .. rollbackErrors]);
        }
        finally
        {
            plan.Dispose();
            current.Dispose();
        }
    }

    private async Task<List<PortableKeyFile>> CaptureKeysAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var keys = new List<PortableKeyFile>();
        long totalBytes = 0;
        foreach (var identity in config.Identities)
        {
            await CaptureKeyAsync(identity, PortableKeyKind.Private, identity.PrivateKeyPath).ConfigureAwait(false);
            await CaptureKeyAsync(identity, PortableKeyKind.Public, identity.PublicKeyPath).ConfigureAwait(false);
        }

        return keys;

        async Task CaptureKeyAsync(GitIdentity identity, PortableKeyKind kind, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            if (!_fileSystem.FileExists(sourcePath))
            {
                throw new InvalidDataException(
                    $"The {kind.ToString().ToLowerInvariant()} key configured for identity '{identity.DisplayName}' was not found.");
            }

            var bytes = await _fileSystem.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (bytes.Length > MaximumKeyBytes)
                {
                    throw new InvalidDataException(
                        $"A configured {kind.ToString().ToLowerInvariant()} key exceeds the 16 MiB per-file limit.");
                }

                totalBytes += bytes.LongLength;
                if (totalBytes > MaximumTotalKeyBytes)
                {
                    throw new InvalidDataException("Configured key files exceed the 48 MiB portable-backup total limit.");
                }

                keys.Add(new PortableKeyFile
                {
                    IdentityId = identity.Id,
                    Kind = kind,
                    SourcePath = sourcePath,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    Content = Convert.ToBase64String(bytes)
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private async Task<OperationResult<ReadPackageResult>> ReadPackageAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !_fileSystem.FileExists(packagePath))
        {
            return OperationResult<ReadPackageResult>.Fail("The portable backup package was not found.");
        }

        if (string.IsNullOrEmpty(password))
        {
            return OperationResult<ReadPackageResult>.Fail("Enter the portable backup password.");
        }

        byte[]? packageBytes = null;
        byte[]? plaintext = null;
        byte[]? key = null;
        try
        {
            packageBytes = await _fileSystem.ReadAllBytesAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (packageBytes.Length == 0 || packageBytes.Length > MaximumPackageBytes)
            {
                throw new InvalidDataException("The portable backup package has an invalid size.");
            }

            var envelope = JsonSerializer.Deserialize<PortableEnvelope>(packageBytes, JsonOptions)
                ?? throw new InvalidDataException("The portable backup envelope is empty.");
            ValidateEnvelope(envelope);
            var salt = Convert.FromBase64String(envelope.Salt);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            if (salt.Length != SaltLength || nonce.Length != NonceLength || tag.Length != TagLength)
            {
                throw new InvalidDataException("The portable backup encryption parameters are invalid.");
            }

            if (ciphertext.Length == 0 || ciphertext.Length > MaximumPackageBytes)
            {
                throw new InvalidDataException("The portable backup encrypted payload has an invalid size.");
            }

            plaintext = new byte[ciphertext.Length];
            key = DeriveKey(password, salt, envelope.Iterations);
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            }

            var payload = JsonSerializer.Deserialize<PortablePayload>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The portable backup payload is empty.");
            ValidatePayload(payload);
            return OperationResult<ReadPackageResult>.Ok(
                new ReadPackageResult(payload, packageBytes.LongLength));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CryptographicException)
        {
            return OperationResult<ReadPackageResult>.Fail(
                "The portable backup password is incorrect or the package has been modified.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or FormatException)
        {
            return OperationResult<ReadPackageResult>.Fail(
                "The portable backup package could not be validated. No changes were made.",
                exception.Message);
        }
        finally
        {
            if (packageBytes is not null)
            {
                CryptographicOperations.ZeroMemory(packageBytes);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static void ValidateEnvelope(PortableEnvelope envelope)
    {
        if (!string.Equals(envelope.Format, EnvelopeFormat, StringComparison.Ordinal)
            || envelope.Version != EnvelopeVersion
            || !string.Equals(envelope.Kdf, "PBKDF2-SHA256", StringComparison.Ordinal)
            || !string.Equals(envelope.Cipher, "AES-256-GCM", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The portable backup format or encryption algorithm is unsupported.");
        }

        if (envelope.Iterations < 100_000 || envelope.Iterations > 2_000_000)
        {
            throw new InvalidDataException("The portable backup key-derivation parameters are outside the supported range.");
        }
    }

    private static void ValidatePayload(PortablePayload payload)
    {
        if (payload.SchemaVersion != PayloadVersion)
        {
            throw new InvalidDataException($"Portable backup payload schema {payload.SchemaVersion} is unsupported.");
        }

        if (payload.AppConfig is null)
        {
            throw new InvalidDataException("The portable backup does not contain application configuration.");
        }

        if (payload.AppConfig.SchemaVersion < 1
            || payload.AppConfig.SchemaVersion > AppConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Application configuration schema {payload.AppConfig.SchemaVersion} is unsupported by this version.");
        }

        payload.AppConfig.Normalize();
        payload.GitUrlRewrites ??= [];
        payload.Keys ??= [];
        var identities = new Dictionary<string, GitIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in payload.AppConfig.Identities)
        {
            if (string.IsNullOrWhiteSpace(identity.Id) || !identities.TryAdd(identity.Id, identity))
            {
                throw new InvalidDataException("The portable backup contains an invalid or duplicate identity ID.");
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var key in payload.Keys)
        {
            if (!identities.ContainsKey(key.IdentityId))
            {
                throw new InvalidDataException("A portable key file references an unknown identity.");
            }

            if (!seen.Add($"{key.IdentityId}\n{key.Kind}"))
            {
                throw new InvalidDataException("The portable backup contains a duplicate identity key entry.");
            }

            var bytes = Convert.FromBase64String(key.Content);
            try
            {
                if (bytes.Length == 0 || bytes.Length > MaximumKeyBytes)
                {
                    throw new InvalidDataException("A portable key file has an invalid size.");
                }

                totalBytes += bytes.LongLength;
                if (totalBytes > MaximumTotalKeyBytes)
                {
                    throw new InvalidDataException("Portable key files exceed the 48 MiB total limit.");
                }

                var actual = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(actual, key.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A portable key file failed its SHA-256 integrity check.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        foreach (var identity in payload.AppConfig.Identities)
        {
            if (!string.IsNullOrWhiteSpace(identity.PrivateKeyPath)
                && !seen.Contains($"{identity.Id}\n{PortableKeyKind.Private}"))
            {
                throw new InvalidDataException($"The private key for identity '{identity.DisplayName}' is missing.");
            }

            if (!string.IsNullOrWhiteSpace(identity.PublicKeyPath)
                && !seen.Contains($"{identity.Id}\n{PortableKeyKind.Public}"))
            {
                throw new InvalidDataException($"The public key for identity '{identity.DisplayName}' is missing.");
            }
        }
    }

    private ImportPlan BuildImportPlan(PortablePayload payload)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(payload.AppConfig, JsonOptions),
            JsonOptions) ?? throw new InvalidDataException("Could not clone portable application configuration.");
        config.Normalize();

        var managedRoot = Path.GetFullPath(Path.Combine(_paths.SshDirectory, "GitKeyRouter"));
        var identities = config.Identities.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var plannedKeys = new List<PlannedKey>();
        var pathMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in payload.Keys)
        {
            var identity = identities[key.IdentityId];
            var directory = Path.Combine(managedRoot, BuildIdentityDirectoryName(identity.Id));
            var targetPath = Path.GetFullPath(Path.Combine(
                directory,
                key.Kind == PortableKeyKind.Private ? "private-key" : "public-key.pub"));
            EnsureUnderRoot(managedRoot, targetPath);
            var bytes = Convert.FromBase64String(key.Content);
            plannedKeys.Add(new PlannedKey(targetPath, bytes));
            if (!string.IsNullOrWhiteSpace(key.SourcePath))
            {
                pathMappings[key.SourcePath] = targetPath;
            }

            if (key.Kind == PortableKeyKind.Private)
            {
                identity.PrivateKeyPath = targetPath;
            }
            else
            {
                identity.PublicKeyPath = targetPath;
            }
        }

        var sshConfig = RemapSshConfig(payload.SshConfigText, pathMappings);
        var rewrites = payload.GitUrlRewrites
            .Select(rule => new GitUrlRewriteRule(rule.BaseUrl, rule.InsteadOfUrl))
            .ToList();
        return new ImportPlan(config, sshConfig, rewrites, plannedKeys, managedRoot);
    }

    private async Task<CurrentState> CaptureCurrentStateAsync(
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        var config = await CaptureFileAsync(_paths.ConfigPath, cancellationToken).ConfigureAwait(false);
        var ssh = await CaptureFileAsync(_paths.SshConfigPath, cancellationToken).ConfigureAwait(false);
        var keys = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in plan.Keys)
        {
            keys[key.TargetPath] = await CaptureFileAsync(key.TargetPath, cancellationToken).ConfigureAwait(false);
        }

        var rewrites = await _gitStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return new CurrentState(config, ssh, rewrites.ToList(), keys);
    }

    private async Task<FileState> CaptureFileAsync(string path, CancellationToken cancellationToken)
        => _fileSystem.FileExists(path)
            ? new FileState(true, await _fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))
            : new FileState(false, []);

    private async Task<List<string>> RollbackAsync(CurrentState state, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var (path, file) in state.KeyFiles)
        {
            await TryRollbackAsync($"key file '{path}'", () => RestoreFileAsync(path, file, cancellationToken)).ConfigureAwait(false);
        }

        await TryRollbackAsync(
            "application configuration",
            () => RestoreFileAsync(_paths.ConfigPath, state.ConfigFile, cancellationToken)).ConfigureAwait(false);
        await TryRollbackAsync(
            "SSH config",
            () => RestoreFileAsync(_paths.SshConfigPath, state.SshConfigFile, cancellationToken)).ConfigureAwait(false);
        await TryRollbackAsync(
            "Git URL rewrites",
            async () =>
            {
                var result = await ReplaceGitRewritesAsync(state.GitUrlRewrites, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException(string.Join(
                        Environment.NewLine,
                        [result.Message, .. result.Errors]));
                }
            }).ConfigureAwait(false);
        await TryRollbackAsync(
            "Git Profile files",
            async () =>
            {
                var result = await _gitProfiles.ApplyCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException(string.Join(
                        Environment.NewLine,
                        [result.Message, .. result.Errors]));
                }
            }).ConfigureAwait(false);
        return errors;

        async Task TryRollbackAsync(string scope, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add($"Failed to roll back {scope}: {exception.Message}");
            }
        }
    }

    private async Task RestoreFileAsync(string path, FileState state, CancellationToken cancellationToken)
    {
        if (!state.Existed)
        {
            _fileSystem.DeleteFile(path);
            return;
        }

        await _fileSystem.WriteAllBytesAtomicAsync(path, state.Bytes, cancellationToken).ConfigureAwait(false);
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

        var keys = current.Select(rule => rule.ConfigKey)
            .Concat(targetRules.Select(rule => rule.ConfigKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in keys)
        {
            var remove = await _gitStore.RemoveAllForKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (!remove.Succeeded && remove.ExitCode != 5)
            {
                return OperationResult.Fail("Failed to remove an existing Git URL rewrite.", remove.StandardError);
            }
        }

        foreach (var rule in targetRules)
        {
            var add = await _gitStore.AddAsync(
                new GitUrlRewriteRule(rule.BaseUrl, rule.InsteadOfUrl),
                cancellationToken).ConfigureAwait(false);
            if (!add.Succeeded)
            {
                return OperationResult.Fail("Failed to restore a Git URL rewrite.", add.StandardError);
            }
        }

        var actual = await _gitStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!NormalizeRules(actual).SequenceEqual(NormalizeRules(targetRules), StringComparer.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("Git URL rewrites did not match the portable backup after restoration.");
        }

        return OperationResult.Ok("Git URL rewrites restored.");
    }

    private static IEnumerable<string> NormalizeRules(IEnumerable<GitUrlRewriteRule> rules)
        => rules.Select(rule => $"{rule.ConfigKey}\n{rule.InsteadOfUrl}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

    private static string? RemapSshConfig(
        string? sshConfig,
        IReadOnlyDictionary<string, string> mappings)
    {
        if (sshConfig is null)
        {
            return null;
        }

        var result = sshConfig;
        foreach (var mapping in mappings.OrderByDescending(item => item.Key.Length))
        {
            result = result.Replace(mapping.Key, mapping.Value, StringComparison.OrdinalIgnoreCase);
            var sourceGitPath = mapping.Key.Replace('\\', '/');
            var targetGitPath = mapping.Value.Replace('\\', '/');
            result = result.Replace(sourceGitPath, targetGitPath, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string BuildIdentityDirectoryName(string identityId)
    {
        var safe = new string(identityId
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .Take(48)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "identity";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityId)))[..12]
            .ToLowerInvariant();
        return $"{safe}-{hash}";
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A portable key path escaped the managed SSH key directory.");
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static PortableBackupPreview CreatePreview(PortablePayload payload, long packageBytes)
        => new()
        {
            CreatedAt = payload.CreatedAt,
            ApplicationSchemaVersion = payload.AppConfig.SchemaVersion,
            IdentityCount = payload.AppConfig.Identities.Count,
            KeyFileCount = payload.Keys.Count,
            GitRewriteCount = payload.GitUrlRewrites.Count,
            HasSshConfig = payload.SshConfigText is not null,
            PackageBytes = packageBytes
        };

    private sealed class PortableEnvelope
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Kdf { get; set; } = string.Empty;
        public int Iterations { get; set; }
        public string Cipher { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }

    private sealed class PortablePayload
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public AppConfig AppConfig { get; set; } = new();
        public string? SshConfigText { get; set; }
        public List<GitUrlRewriteRule> GitUrlRewrites { get; set; } = [];
        public List<PortableKeyFile> Keys { get; set; } = [];
    }

    private sealed class PortableKeyFile
    {
        public string IdentityId { get; set; } = string.Empty;
        public PortableKeyKind Kind { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    private enum PortableKeyKind
    {
        Private,
        Public
    }

    private sealed record ReadPackageResult(PortablePayload Payload, long PackageBytes);

    private sealed class ImportPlan : IDisposable
    {
        public ImportPlan(
            AppConfig config,
            string? sshConfigText,
            List<GitUrlRewriteRule> gitUrlRewrites,
            List<PlannedKey> keys,
            string managedKeyDirectory)
        {
            Config = config;
            SshConfigText = sshConfigText;
            GitUrlRewrites = gitUrlRewrites;
            Keys = keys;
            ManagedKeyDirectory = managedKeyDirectory;
        }

        public AppConfig Config { get; }
        public string? SshConfigText { get; }
        public List<GitUrlRewriteRule> GitUrlRewrites { get; }
        public List<PlannedKey> Keys { get; }
        public string ManagedKeyDirectory { get; }

        public void Dispose()
        {
            foreach (var key in Keys)
            {
                key.Dispose();
            }
        }
    }

    private sealed class PlannedKey : IDisposable
    {
        public PlannedKey(string targetPath, byte[] bytes)
        {
            TargetPath = targetPath;
            Bytes = bytes;
        }

        public string TargetPath { get; }
        public byte[] Bytes { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }

    private sealed class CurrentState : IDisposable
    {
        public CurrentState(
            FileState configFile,
            FileState sshConfigFile,
            List<GitUrlRewriteRule> gitUrlRewrites,
            Dictionary<string, FileState> keyFiles)
        {
            ConfigFile = configFile;
            SshConfigFile = sshConfigFile;
            GitUrlRewrites = gitUrlRewrites;
            KeyFiles = keyFiles;
        }

        public FileState ConfigFile { get; }
        public FileState SshConfigFile { get; }
        public List<GitUrlRewriteRule> GitUrlRewrites { get; }
        public Dictionary<string, FileState> KeyFiles { get; }

        public void Dispose()
        {
            ConfigFile.Dispose();
            SshConfigFile.Dispose();
            foreach (var file in KeyFiles.Values)
            {
                file.Dispose();
            }
        }
    }

    private sealed class FileState : IDisposable
    {
        public FileState(bool existed, byte[] bytes)
        {
            Existed = existed;
            Bytes = bytes;
        }

        public bool Existed { get; }
        public byte[] Bytes { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }
}
