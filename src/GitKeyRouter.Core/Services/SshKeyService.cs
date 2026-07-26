using System.Text.RegularExpressions;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed partial class SshKeyService
{
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _processRunner;
    private readonly IToolchainService _toolchainService;
    private readonly IClock _clock;
    private readonly GitProviderAdapterRegistry _providers;

    public SshKeyService(
        IFileSystem fileSystem,
        IProcessRunner processRunner,
        IToolchainService toolchainService,
        IClock clock,
        GitProviderAdapterRegistry? providers = null)
    {
        _fileSystem = fileSystem;
        _processRunner = processRunner;
        _toolchainService = toolchainService;
        _clock = clock;
        _providers = providers ?? GitProviderAdapterRegistry.CreateDefault();
    }

    public static string CreateDefaultPrivateKeyPath(string sshDirectory, string hostAlias)
    {
        var validation = HostAliasValidator.Validate(hostAlias);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors), nameof(hostAlias));
        }

        var normalizedAlias = NonFileNameCharacterPattern().Replace(hostAlias, "_");
        return Path.Combine(sshDirectory, $"id_ed25519_{normalizedAlias}");
    }

    public bool PrivateKeyExists(GitIdentity identity) => _fileSystem.FileExists(identity.PrivateKeyPath);

    public bool PublicKeyExists(GitIdentity identity) => _fileSystem.FileExists(identity.PublicKeyPath);

    public async Task<OperationResult<IReadOnlyList<SshPrivateKeyCandidate>>> DiscoverPrivateKeysAsync(
        string sshDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sshDirectory) || !_fileSystem.DirectoryExists(sshDirectory))
        {
            return OperationResult<IReadOnlyList<SshPrivateKeyCandidate>>.Ok(
                [],
                "SSH directory does not exist; no private keys were discovered.");
        }

        try
        {
            var candidates = new List<SshPrivateKeyCandidate>();
            foreach (var path in _fileSystem.EnumerateFiles(sshDirectory, "*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipPrivateKeyCandidate(path))
                {
                    continue;
                }

                try
                {
                    var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    var inspection = SshKeyFormatDetector.Detect(text, path);
                    if (!inspection.IsPrivateMaterial)
                    {
                        continue;
                    }

                    candidates.Add(new SshPrivateKeyCandidate
                    {
                        Path = Path.GetFullPath(path),
                        FileName = Path.GetFileName(path),
                        Inspection = inspection
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Ignore unreadable or non-text files. Discovery must not block identity editing.
                }
            }

            var ordered = candidates
                .OrderByDescending(item => item.FileName.StartsWith("id_", StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return OperationResult<IReadOnlyList<SshPrivateKeyCandidate>>.Ok(
                ordered,
                $"Discovered {ordered.Count} private-key candidate(s).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult<IReadOnlyList<SshPrivateKeyCandidate>>.Fail(
                "Unable to scan the SSH directory for private keys.",
                exception.Message);
        }
    }

    public async Task<OperationResult<IReadOnlyList<SshPublicKeyVariant>>> ListPublicKeyVariantsAsync(
        GitIdentity identity,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_fileSystem.FileExists(identity.PublicKeyPath))
            {
                paths.Add(Path.GetFullPath(identity.PublicKeyPath));
            }

            var directory = Path.GetDirectoryName(identity.PublicKeyPath);
            var stem = GetPublicKeyVariantStem(identity.PublicKeyPath);
            if (!string.IsNullOrWhiteSpace(directory) && _fileSystem.DirectoryExists(directory))
            {
                foreach (var path in _fileSystem.EnumerateFiles(directory, $"{stem}*"))
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.Contains(".gitkeyrouter.", StringComparison.OrdinalIgnoreCase)
                        || fileName.StartsWith(".gitkeyrouter-", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    paths.Add(Path.GetFullPath(path));
                }
            }

            var variants = new List<SshPublicKeyVariant>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var inspection = SshKeyFormatDetector.Detect(text, path);
                if (inspection.IsPrivateMaterial)
                {
                    continue;
                }

                variants.Add(new SshPublicKeyVariant
                {
                    Path = path,
                    FileName = Path.GetFileName(path),
                    Inspection = inspection,
                    IsConfiguredPath = PathsEqual(path, identity.PublicKeyPath)
                });
            }

            return OperationResult<IReadOnlyList<SshPublicKeyVariant>>.Ok(
                variants
                    .OrderByDescending(item => item.IsConfiguredPath)
                    .ThenBy(item => item.Inspection.Format)
                    .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                $"Detected {variants.Count} public-key variant(s).");
        }
        catch (Exception exception)
        {
            return OperationResult<IReadOnlyList<SshPublicKeyVariant>>.Fail(
                "Unable to enumerate public-key variants.",
                exception.Message);
        }
    }

    public async Task<OperationResult<SshKeyInspectionResult>> InspectKeyFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!_fileSystem.FileExists(path))
        {
            return OperationResult<SshKeyInspectionResult>.Fail("Key file does not exist.", path);
        }

        try
        {
            var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return OperationResult<SshKeyInspectionResult>.Ok(
                SshKeyFormatDetector.Detect(text, path),
                "Key format detected.");
        }
        catch (Exception exception)
        {
            return OperationResult<SshKeyInspectionResult>.Fail("Unable to inspect the key file.", exception.Message);
        }
    }

    public Task<OperationResult<string>> ReadPublicKeyAsync(
        GitIdentity identity,
        CancellationToken cancellationToken = default)
        => ReadPublicKeyAsync(identity.PublicKeyPath, requireOpenSsh: false, cancellationToken);

    public async Task<OperationResult<string>> ReadPublicKeyAsync(
        string path,
        bool requireOpenSsh,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectKeyFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!inspection.Success || inspection.Value is null)
        {
            return OperationResult<string>.Fail(inspection.Message, inspection.Errors.ToArray());
        }

        if (inspection.Value.IsPrivateMaterial)
        {
            return OperationResult<string>.Fail(
                "Private key material will not be displayed or copied.",
                "Use format conversion to derive a separate public-key file.");
        }

        if (requireOpenSsh && !inspection.Value.IsOpenSsh)
        {
            return OperationResult<string>.Fail(
                $"The selected key is {inspection.Value.DisplayName}, not an OpenSSH public key.",
                inspection.Value.CanConvert
                    ? "Convert it to OpenSSH before copying it to GitHub."
                    : "Select a valid OpenSSH public-key file.");
        }

        try
        {
            var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return OperationResult<string>.Ok(
                inspection.Value.IsOpenSsh ? inspection.Value.PublicKeyText : text.Trim(),
                $"{inspection.Value.DisplayName} loaded.");
        }
        catch (Exception exception)
        {
            return OperationResult<string>.Fail("Unable to read the public key.", exception.Message);
        }
    }

    public async Task<OperationResult<SshKeyGenerationResult>> GenerateAsync(
        GitIdentity identity,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.SshKeygen.Exists || string.IsNullOrWhiteSpace(tools.SshKeygen.SelectedPath))
        {
            return OperationResult<SshKeyGenerationResult>.Fail(
                "ssh-keygen.exe was not found.",
                "Enable the Windows OpenSSH Client or install Git for Windows. GitKeyRouter will not install it automatically.");
        }

        if (PathsEqual(identity.PrivateKeyPath, identity.PublicKeyPath))
        {
            return OperationResult<SshKeyGenerationResult>.Fail(
                "The private-key and public-key paths must be different.");
        }

        var targetPaths = new[] { identity.PrivateKeyPath, identity.PublicKeyPath };
        var existingFiles = targetPaths
            .Where(_fileSystem.FileExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (existingFiles.Count > 0 && !overwrite)
        {
            return OperationResult<SshKeyGenerationResult>.Fail(
                "The target key file already exists. Choose another filename or explicitly confirm overwrite.",
                existingFiles.ToArray());
        }

        var privateKeyDirectory = Path.GetDirectoryName(identity.PrivateKeyPath);
        var publicKeyDirectory = Path.GetDirectoryName(identity.PublicKeyPath);
        if (string.IsNullOrWhiteSpace(privateKeyDirectory) || string.IsNullOrWhiteSpace(publicKeyDirectory))
        {
            return OperationResult<SshKeyGenerationResult>.Fail("The key paths must have valid parent directories.");
        }

        _fileSystem.CreateDirectory(privateKeyDirectory);
        _fileSystem.CreateDirectory(publicKeyDirectory);
        var temporaryPrivatePath = Path.Combine(
            privateKeyDirectory,
            $".{Path.GetFileName(identity.PrivateKeyPath)}.gitkeyrouter.{Guid.NewGuid():N}.tmp");
        var temporaryPublicPath = temporaryPrivatePath + ".pub";
        var backups = new List<string>();
        var backupByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var promotedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originallyExisting = existingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var comment = string.IsNullOrWhiteSpace(identity.EmailOrComment)
                ? identity.AccountName
                : identity.EmailOrComment;
            var process = await _processRunner.RunAsync(new ProcessRequest
            {
                ExecutablePath = tools.SshKeygen.SelectedPath,
                Arguments = ["-t", "ed25519", "-C", comment, "-f", temporaryPrivatePath, "-N", string.Empty],
                Timeout = TimeSpan.FromSeconds(30)
            }, cancellationToken).ConfigureAwait(false);

            if (!process.Succeeded)
            {
                return OperationResult<SshKeyGenerationResult>.Fail(
                    "ssh-keygen failed. Existing key files were left unchanged.",
                    $"Exit code: {process.ExitCode}",
                    process.StandardOutput,
                    process.StandardError);
            }

            if (!_fileSystem.FileExists(temporaryPrivatePath) || !_fileSystem.FileExists(temporaryPublicPath))
            {
                return OperationResult<SshKeyGenerationResult>.Fail(
                    "ssh-keygen did not produce a complete key pair. Existing key files were left unchanged.",
                    !_fileSystem.FileExists(temporaryPrivatePath)
                        ? "The generated private-key file is missing."
                        : "The generated public-key file is missing.");
            }

            var publicKey = await ReadPublicKeyAsync(
                temporaryPublicPath,
                requireOpenSsh: true,
                cancellationToken).ConfigureAwait(false);
            if (!publicKey.Success || string.IsNullOrWhiteSpace(publicKey.Value))
            {
                return OperationResult<SshKeyGenerationResult>.Fail(
                    "The generated public key could not be validated. Existing key files were left unchanged.",
                    publicKey.Errors.Prepend(publicKey.Message).ToArray());
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite)
            {
                foreach (var existingFile in existingFiles)
                {
                    var backupPath = $"{existingFile}.gitkeyrouter.{_clock.LocalNow:yyyyMMdd-HHmmss}.{Guid.NewGuid():N}.bak";
                    _fileSystem.CopyFile(existingFile, backupPath, false);
                    backups.Add(backupPath);
                    backupByTarget[existingFile] = backupPath;
                }
            }

            PromoteGeneratedFile(
                temporaryPrivatePath,
                identity.PrivateKeyPath,
                originallyExisting.Contains(identity.PrivateKeyPath),
                promotedTargets);
            PromoteGeneratedFile(
                temporaryPublicPath,
                identity.PublicKeyPath,
                originallyExisting.Contains(identity.PublicKeyPath),
                promotedTargets);

            return OperationResult<SshKeyGenerationResult>.Ok(new SshKeyGenerationResult
            {
                Identity = identity,
                Process = process,
                PublicKeyText = publicKey.Value,
                BackupFiles = backups
            }, "SSH key generated. The key has no passphrase.");
        }
        catch (OperationCanceledException)
        {
            var rollbackErrors = RollBackGeneratedFiles(targetPaths, existingFiles, backupByTarget, promotedTargets);
            return OperationResult<SshKeyGenerationResult>.Fail(
                "SSH key generation was cancelled. Existing key files were restored.",
                rollbackErrors.ToArray());
        }
        catch (Exception exception)
        {
            var rollbackErrors = RollBackGeneratedFiles(targetPaths, existingFiles, backupByTarget, promotedTargets);
            return OperationResult<SshKeyGenerationResult>.Fail(
                "Unable to generate the SSH key pair. Existing key files were restored.",
                new[] { exception.Message }.Concat(rollbackErrors).ToArray());
        }
        finally
        {
            TryDeleteFile(temporaryPrivatePath);
            TryDeleteFile(temporaryPublicPath);
        }
    }

    private void PromoteGeneratedFile(
        string temporaryPath,
        string targetPath,
        bool overwrite,
        ISet<string> promotedTargets)
    {
        _fileSystem.MoveFile(temporaryPath, targetPath, overwrite);
        promotedTargets.Add(targetPath);
    }

    private List<string> RollBackGeneratedFiles(
        IEnumerable<string> targetPaths,
        IReadOnlyCollection<string> existingFiles,
        IReadOnlyDictionary<string, string> backupByTarget,
        IReadOnlySet<string> promotedTargets)
    {
        var rollbackErrors = new List<string>();
        var originallyExisting = existingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var targetPath in targetPaths.Reverse())
        {
            try
            {
                if (originallyExisting.Contains(targetPath))
                {
                    if (backupByTarget.TryGetValue(targetPath, out var backupPath))
                    {
                        _fileSystem.CopyFile(backupPath, targetPath, true);
                    }
                }
                else if (promotedTargets.Contains(targetPath))
                {
                    _fileSystem.DeleteFile(targetPath);
                }
            }
            catch (Exception exception)
            {
                rollbackErrors.Add($"Unable to restore '{targetPath}': {exception.Message}");
            }
        }

        return rollbackErrors;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch
        {
            // Best-effort cleanup must not hide the generation or rollback result.
        }
    }

    public async Task<OperationResult<SshKeyConversionResult>> ConvertPublicKeyAsync(
        GitIdentity identity,
        string sourcePath,
        SshPublicKeyExportFormat targetFormat,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.SshKeygen.Exists || string.IsNullOrWhiteSpace(tools.SshKeygen.SelectedPath))
        {
            return OperationResult<SshKeyConversionResult>.Fail(
                "ssh-keygen.exe was not found.",
                "Enable the Windows OpenSSH Client or install Git for Windows.");
        }

        var inspection = await InspectKeyFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!inspection.Success || inspection.Value is null)
        {
            return OperationResult<SshKeyConversionResult>.Fail(inspection.Message, inspection.Errors.ToArray());
        }

        var source = inspection.Value;
        if (source.IsPrivateMaterial && !PathsEqual(sourcePath, identity.PrivateKeyPath))
        {
            return OperationResult<SshKeyConversionResult>.Fail(
                "Private key data was found outside the configured private-key path. Conversion was refused.",
                "Correct the identity paths before converting.");
        }

        if (!source.CanConvert || source.Format is SshKeyFormat.PuttyPrivate or SshKeyFormat.Unknown)
        {
            return OperationResult<SshKeyConversionResult>.Fail(
                $"{source.DisplayName} cannot be converted by the available OpenSSH toolchain.",
                source.Format == SshKeyFormat.PuttyPrivate
                    ? "Convert the PPK with PuTTYgen first."
                    : "Select an OpenSSH, RFC4716, PEM/PKCS public key, or the configured private key.");
        }

        var openSsh = await ConvertToOpenSshAsync(
            tools.SshKeygen.SelectedPath,
            source,
            cancellationToken).ConfigureAwait(false);
        if (!openSsh.Result.Success || string.IsNullOrWhiteSpace(openSsh.Result.Value))
        {
            return OperationResult<SshKeyConversionResult>.Fail(openSsh.Result.Message, openSsh.Result.Errors.ToArray());
        }

        var convertedText = openSsh.Result.Value;
        ProcessResult? exportProcess = null;
        if (targetFormat != SshPublicKeyExportFormat.OpenSsh)
        {
            var exported = await ExportFromOpenSshAsync(
                tools.SshKeygen.SelectedPath,
                openSsh.Result.Value,
                targetFormat,
                identity.PublicKeyPath,
                cancellationToken).ConfigureAwait(false);
            if (!exported.Result.Success || string.IsNullOrWhiteSpace(exported.Result.Value))
            {
                return OperationResult<SshKeyConversionResult>.Fail(exported.Result.Message, exported.Result.Errors.ToArray());
            }

            convertedText = exported.Result.Value;
            exportProcess = exported.Process;
        }

        var destinationPath = GetVariantPath(identity.PublicKeyPath, targetFormat);
        var converted = SshKeyFormatDetector.Detect(convertedText, destinationPath);
        if (targetFormat == SshPublicKeyExportFormat.OpenSsh && !converted.IsOpenSsh)
        {
            return OperationResult<SshKeyConversionResult>.Fail("The conversion output is not a valid OpenSSH public key.");
        }

        string? backupFile = null;
        if (_fileSystem.FileExists(destinationPath))
        {
            var existing = await _fileSystem.ReadAllTextAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing.Trim(), convertedText.Trim(), StringComparison.Ordinal))
            {
                return OperationResult<SshKeyConversionResult>.Ok(new SshKeyConversionResult
                {
                    Source = source,
                    Converted = converted,
                    DestinationPath = destinationPath,
                    ImportProcess = openSsh.Process,
                    ExportProcess = exportProcess,
                    Changed = false
                }, "The requested public-key variant already exists with identical content.");
            }

            if (!overwrite)
            {
                return OperationResult<SshKeyConversionResult>.Fail(
                    "The target public-key variant already exists.",
                    destinationPath);
            }

            backupFile = $"{destinationPath}.gitkeyrouter.{_clock.LocalNow:yyyyMMdd-HHmmss}.bak";
            _fileSystem.CopyFile(destinationPath, backupFile, false);
        }

        await _fileSystem.WriteAllTextAtomicAsync(
            destinationPath,
            convertedText.Trim() + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);

        return OperationResult<SshKeyConversionResult>.Ok(new SshKeyConversionResult
        {
            Source = source,
            Converted = converted,
            DestinationPath = destinationPath,
            ImportProcess = openSsh.Process,
            ExportProcess = exportProcess,
            BackupFile = backupFile,
            Changed = true
        }, $"Created {converted.DisplayName}: {destinationPath}");
    }

    public Task<OperationResult> ExportPublicKeyAsync(
        GitIdentity identity,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
        => ExportPublicKeyAsync(identity.PublicKeyPath, destinationPath, overwrite, cancellationToken);

    public async Task<OperationResult> ExportPublicKeyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectKeyFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!inspection.Success || inspection.Value is null)
        {
            return OperationResult.Fail(inspection.Message, inspection.Errors.ToArray());
        }

        if (inspection.Value.IsPrivateMaterial)
        {
            return OperationResult.Fail("Private key material cannot be exported through the public-key action.");
        }

        if (_fileSystem.FileExists(destinationPath) && !overwrite)
        {
            return OperationResult.Fail("The export destination already exists.", destinationPath);
        }

        await Task.Run(() => _fileSystem.CopyFile(sourcePath, destinationPath, overwrite), cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"{inspection.Value.DisplayName} exported.");
    }

    public async Task<SshTestResult> TestAsync(string hostAlias, bool verbose, CancellationToken cancellationToken = default)
        => await TestAsync(
            GitServiceInstance.CreateGitHubCom(),
            new GitIdentity
            {
                ServiceInstanceId = GitServiceInstance.GitHubComId,
                HostAlias = hostAlias
            },
            verbose,
            cancellationToken).ConfigureAwait(false);

    public async Task<SshTestResult> TestAsync(
        GitServiceInstance service,
        GitIdentity identity,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The identity belongs to a different Git service.", nameof(identity));
        }

        var validation = HostAliasValidator.Validate(identity.HostAlias);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors), nameof(identity));
        }

        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.Ssh.Exists || string.IsNullOrWhiteSpace(tools.Ssh.SelectedPath))
        {
            var missing = new ProcessResult
            {
                ExecutablePath = "ssh.exe",
                StandardError = "ssh.exe was not found. GitKeyRouter did not attempt to install it."
            };
            return new SshTestResult
            {
                HostAlias = identity.HostAlias,
                Process = missing,
                Classification = "ssh.exe missing"
            };
        }

        var arguments = verbose
            ? new[] { "-vT", $"{service.SshUser}@{identity.HostAlias}" }
            : new[] { "-T", $"{service.SshUser}@{identity.HostAlias}" };
        var process = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = tools.Ssh.SelectedPath,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        }, cancellationToken).ConfigureAwait(false);
        var combined = process.StandardOutput + Environment.NewLine + process.StandardError;
        var authenticated = _providers.Get(service.ProviderKind).IsAuthenticationSuccess(process);
        return new SshTestResult
        {
            HostAlias = identity.HostAlias,
            Process = process,
            Authenticated = authenticated,
            Classification = Classify(service, combined, authenticated, process)
        };
    }

    private async Task<(OperationResult<string> Result, ProcessResult? Process)> ConvertToOpenSshAsync(
        string sshKeygenPath,
        SshKeyInspectionResult source,
        CancellationToken cancellationToken)
    {
        if (source.IsOpenSsh)
        {
            return (OperationResult<string>.Ok(source.PublicKeyText), null);
        }

        IReadOnlyList<string> arguments = source.Format switch
        {
            SshKeyFormat.Rfc4716Public => ["-i", "-m", "RFC4716", "-f", source.SourcePath],
            SshKeyFormat.PemPublic => ["-i", "-m", "PKCS8", "-f", source.SourcePath],
            SshKeyFormat.OpenSshPrivate or SshKeyFormat.PemPrivate => ["-y", "-f", source.SourcePath],
            _ => []
        };
        if (arguments.Count == 0)
        {
            return (OperationResult<string>.Fail($"{source.DisplayName} cannot be imported as OpenSSH."), null);
        }

        var process = await RunSshKeygenAsync(sshKeygenPath, arguments, cancellationToken).ConfigureAwait(false);
        if (!process.Succeeded && source.Format == SshKeyFormat.PemPublic)
        {
            process = await RunSshKeygenAsync(
                sshKeygenPath,
                ["-i", "-m", "PEM", "-f", source.SourcePath],
                cancellationToken).ConfigureAwait(false);
        }

        if (!process.Succeeded)
        {
            return (OperationResult<string>.Fail(
                "ssh-keygen could not import the selected key.",
                $"Detected format: {source.DisplayName}",
                $"Exit code: {process.ExitCode}",
                process.StandardError), process);
        }

        if (!SshKeyFormatDetector.TryNormalizeOpenSshPublicKey(process.StandardOutput, out var openSsh, out _))
        {
            return (OperationResult<string>.Fail("ssh-keygen did not return a valid OpenSSH public key."), process);
        }

        return (OperationResult<string>.Ok(openSsh), process);
    }

    private async Task<(OperationResult<string> Result, ProcessResult? Process)> ExportFromOpenSshAsync(
        string sshKeygenPath,
        string openSsh,
        SshPublicKeyExportFormat targetFormat,
        string configuredPublicKeyPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(configuredPublicKeyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return (OperationResult<string>.Fail("The public-key path has no valid parent directory."), null);
        }

        _fileSystem.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".gitkeyrouter-{Guid.NewGuid():N}.openssh.pub");
        try
        {
            await _fileSystem.WriteAllTextAtomicAsync(temporaryPath, openSsh.Trim() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            var format = targetFormat == SshPublicKeyExportFormat.Rfc4716 ? "RFC4716" : "PKCS8";
            var process = await RunSshKeygenAsync(
                sshKeygenPath,
                ["-e", "-m", format, "-f", temporaryPath],
                cancellationToken).ConfigureAwait(false);
            if (!process.Succeeded)
            {
                return (OperationResult<string>.Fail(
                    "ssh-keygen could not export the requested public-key format.",
                    $"Exit code: {process.ExitCode}",
                    process.StandardError), process);
            }

            return (OperationResult<string>.Ok(process.StandardOutput.Trim()), process);
        }
        finally
        {
            _fileSystem.DeleteFile(temporaryPath);
        }
    }

    private Task<ProcessResult> RunSshKeygenAsync(
        string sshKeygenPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = sshKeygenPath,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        }, cancellationToken);

    public static string GetPublicKeyVariantStem(string configuredPublicKeyPath)
    {
        var stem = Path.GetFileNameWithoutExtension(configuredPublicKeyPath);
        foreach (var suffix in new[] { ".openssh", ".rfc4716", ".pem" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^suffix.Length];
            }
        }

        return stem;
    }

    private static string GetVariantPath(string configuredPublicKeyPath, SshPublicKeyExportFormat format)
    {
        var directory = Path.GetDirectoryName(configuredPublicKeyPath) ?? string.Empty;
        var stem = GetPublicKeyVariantStem(configuredPublicKeyPath);
        var suffix = format switch
        {
            SshPublicKeyExportFormat.OpenSsh => ".openssh.pub",
            SshPublicKeyExportFormat.Rfc4716 => ".rfc4716.pub",
            SshPublicKeyExportFormat.Pem => ".pem.pub",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
        return Path.Combine(directory, stem + suffix);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipPrivateKeyCandidate(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".gitkeyrouter.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Equals("config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("known_hosts", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("known_hosts.old", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("authorized_keys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("environment", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("rc", StringComparison.OrdinalIgnoreCase);
    }

    private static string Classify(
        GitServiceInstance service,
        string output,
        bool authenticated,
        ProcessResult process)
    {
        if (authenticated)
        {
            return $"{service.DisplayName} authentication succeeded";
        }

        if (process.TimedOut)
        {
            return "Connection timed out";
        }

        if (output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Public key was rejected";
        }

        if (output.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
        {
            return "Host alias or DNS resolution failed";
        }

        if (output.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Host key verification failed";
        }

        return "Unknown SSH result";
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonFileNameCharacterPattern();
}
