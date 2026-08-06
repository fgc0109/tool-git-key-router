using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed class SshHostTrustService
{
    private const int MaximumKnownHostsBytes = 4 * 1024 * 1024;
    private const int MaximumScannedKeys = 16;
    private readonly IFileSystem _fileSystem;
    private readonly IAppPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly IToolchainService _toolchainService;
    private readonly IClock _clock;

    public SshHostTrustService(
        IFileSystem fileSystem,
        IAppPaths paths,
        IProcessRunner processRunner,
        IToolchainService toolchainService,
        IClock clock)
    {
        _fileSystem = fileSystem;
        _paths = paths;
        _processRunner = processRunner;
        _toolchainService = toolchainService;
        _clock = clock;
    }

    public async Task<OperationResult<SshHostTrustPreview>> BuildPreviewAsync(
        GitServiceInstance service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var validation = GitServiceValidator.Validate(service, []);
        if (!validation.IsValid)
        {
            return OperationResult<SshHostTrustPreview>.Fail(
                "Git service validation failed.",
                validation.Errors.ToArray());
        }

        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        var keyscanPath = ResolveSiblingExecutable(tools, "ssh-keyscan.exe");
        if (keyscanPath is null)
        {
            return OperationResult<SshHostTrustPreview>.Fail(
                "ssh-keyscan.exe was not found next to the selected OpenSSH tools.");
        }

        var port = service.SshPort ?? 22;
        var hostName = service.HostName.Trim();
        var hostIdentifier = FormatHostIdentifier(hostName, port);
        var scan = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = keyscanPath,
            Arguments =
            [
                "-T", "10",
                "-p", port.ToString(CultureInfo.InvariantCulture),
                hostName
            ],
            Timeout = TimeSpan.FromSeconds(15),
            MaxOutputLines = 64,
            MaxOutputCharactersPerLine = 16 * 1024
        }, cancellationToken).ConfigureAwait(false);
        if (scan.TimedOut || scan.Cancelled || scan.StartException is not null)
        {
            return OperationResult<SshHostTrustPreview>.Fail(
                "Unable to scan the SSH server host keys.",
                scan.StandardError);
        }

        var scannedKeys = ParseScannedKeys(scan.StandardOutput, hostIdentifier);
        if (scannedKeys.Count == 0)
        {
            return OperationResult<SshHostTrustPreview>.Fail(
                "ssh-keyscan did not return a valid host key for the configured endpoint.",
                $"Endpoint: {hostIdentifier}",
                $"Exit code: {scan.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}",
                scan.StandardError);
        }

        var knownHostsPath = Path.Combine(_paths.SshDirectory, "known_hosts");
        var fileVersionResult = await ReadFileVersionAsync(knownHostsPath, cancellationToken).ConfigureAwait(false);
        if (!fileVersionResult.Success || fileVersionResult.Value is null)
        {
            return OperationResult<SshHostTrustPreview>.Fail(
                fileVersionResult.Message,
                fileVersionResult.Errors.ToArray());
        }

        IReadOnlyList<SshHostPublicKey> existingKeys = [];
        var existingEntriesContainMarkers = false;
        if (fileVersionResult.Value.Exists)
        {
            if (!tools.SshKeygen.Exists || string.IsNullOrWhiteSpace(tools.SshKeygen.SelectedPath))
            {
                return OperationResult<SshHostTrustPreview>.Fail(
                    "ssh-keygen.exe is required to inspect existing known_hosts entries.");
            }

            var find = await _processRunner.RunAsync(new ProcessRequest
            {
                ExecutablePath = tools.SshKeygen.SelectedPath,
                Arguments = ["-F", hostIdentifier, "-f", knownHostsPath],
                Timeout = TimeSpan.FromSeconds(10),
                MaxOutputLines = 128,
                MaxOutputCharactersPerLine = 16 * 1024
            }, cancellationToken).ConfigureAwait(false);
            if (find.TimedOut || find.Cancelled || find.StartException is not null
                || (find.ExitCode is not 0 and not 1))
            {
                return OperationResult<SshHostTrustPreview>.Fail(
                    "Unable to inspect the existing known_hosts entries.",
                    find.StandardError);
            }

            existingKeys = ParseExistingKeys(find.StandardOutput, out existingEntriesContainMarkers);
        }

        var status = existingEntriesContainMarkers
            ? SshHostTrustStatus.Conflict
            : DetermineStatus(scannedKeys, existingKeys);
        return OperationResult<SshHostTrustPreview>.Ok(new SshHostTrustPreview
        {
            ServiceInstanceId = service.Id,
            ServiceDisplayName = service.DisplayName,
            HostName = hostName,
            Port = port,
            HostIdentifier = hostIdentifier,
            KnownHostsPath = knownHostsPath,
            Status = status,
            FileVersion = fileVersionResult.Value,
            ScannedKeys = scannedKeys,
            ExistingKeys = existingKeys,
            ExistingEntriesContainMarkers = existingEntriesContainMarkers,
            ScanProcess = scan
        }, "SSH host-key trust preview created.");
    }

    public async Task<OperationResult<SshHostTrustApplyResult>> TrustAsync(
        GitServiceInstance service,
        SshHostTrustPreview expectedPreview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(expectedPreview);
        if (!expectedPreview.CanTrust)
        {
            return OperationResult<SshHostTrustApplyResult>.Fail(
                expectedPreview.Status == SshHostTrustStatus.Conflict
                    ? "Existing known_hosts entries conflict with the scanned server keys and will not be replaced automatically."
                    : "The SSH host is already trusted.");
        }

        if (!EndpointMatches(service, expectedPreview))
        {
            return OperationResult<SshHostTrustApplyResult>.Fail(
                "The Git service endpoint changed after the host-key preview was created.");
        }

        var refreshedResult = await BuildPreviewAsync(service, cancellationToken).ConfigureAwait(false);
        if (!refreshedResult.Success || refreshedResult.Value is null)
        {
            return OperationResult<SshHostTrustApplyResult>.Fail(
                refreshedResult.Message,
                refreshedResult.Errors.ToArray());
        }

        var refreshed = refreshedResult.Value;
        if (refreshed.Status == SshHostTrustStatus.Trusted)
        {
            return OperationResult<SshHostTrustApplyResult>.Ok(new SshHostTrustApplyResult
            {
                KnownHostsPath = refreshed.KnownHostsPath,
                AddedKeyCount = 0,
                TrustedKeys = refreshed.ScannedKeys
            }, "The SSH host was trusted by another operation.");
        }

        if (refreshed.Status == SshHostTrustStatus.Conflict)
        {
            return OperationResult<SshHostTrustApplyResult>.Fail(
                "The known_hosts state now conflicts with the scanned server keys. No file was changed.");
        }

        if (refreshed.FileVersion != expectedPreview.FileVersion
            || !KeySetsEqual(refreshed.ScannedKeys, expectedPreview.ScannedKeys))
        {
            return OperationResult<SshHostTrustApplyResult>.Fail(
                "The server host keys or known_hosts file changed after preview. Review the fingerprints again.");
        }

        var currentText = refreshed.FileVersion.Exists
            ? await _fileSystem.ReadAllTextAsync(refreshed.KnownHostsPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var currentVersionResult = await ReadFileVersionAsync(refreshed.KnownHostsPath, cancellationToken).ConfigureAwait(false);
        if (!currentVersionResult.Success || currentVersionResult.Value != refreshed.FileVersion)
        {
            return OperationResult<SshHostTrustApplyResult>.Fail(
                "known_hosts changed immediately before writing. No file was changed.");
        }

        string? backupPath = null;
        if (refreshed.FileVersion.Exists)
        {
            backupPath = Path.Combine(
                Path.GetDirectoryName(refreshed.KnownHostsPath)!,
                $"known_hosts.gitkeyrouter.{_clock.LocalNow:yyyyMMdd-HHmmss}.{Guid.NewGuid():N}.bak");
            _fileSystem.CopyFile(refreshed.KnownHostsPath, backupPath, overwrite: false);
            var backupBytes = await _fileSystem.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
            var backupSha256 = Convert.ToHexString(SHA256.HashData(backupBytes));
            if (!string.Equals(backupSha256, refreshed.FileVersion.Sha256, StringComparison.Ordinal))
            {
                _fileSystem.DeleteFile(backupPath);
                return OperationResult<SshHostTrustApplyResult>.Fail(
                    "known_hosts changed while its backup was being created. No file was changed.");
            }
        }

        var newLine = currentText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var updated = new StringBuilder(currentText);
        if (updated.Length > 0 && !EndsWithNewLine(updated))
        {
            updated.Append(newLine);
        }

        foreach (var key in refreshed.ScannedKeys.OrderBy(item => item.KeyType, StringComparer.Ordinal))
        {
            updated.Append(refreshed.HostIdentifier)
                .Append(' ')
                .Append(key.KeyType)
                .Append(' ')
                .Append(key.KeyData)
                .Append(newLine);
        }

        OperationResult<SshHostTrustPreview>? verification;
        try
        {
            await _fileSystem.WriteAllTextAtomicAsync(
                refreshed.KnownHostsPath,
                updated.ToString(),
                cancellationToken).ConfigureAwait(false);
            verification = await BuildPreviewAsync(service, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var rollbackMessage = await RollbackKnownHostsAsync(
                refreshed.KnownHostsPath,
                backupPath).ConfigureAwait(false);
            return OperationResult<SshHostTrustApplyResult>.Fail(
                "Unable to write and verify known_hosts. The change was rolled back.",
                exception.Message,
                rollbackMessage);
        }

        if (!verification.Success || verification.Value?.Status != SshHostTrustStatus.Trusted)
        {
            var rollbackMessage = await RollbackKnownHostsAsync(
                refreshed.KnownHostsPath,
                backupPath).ConfigureAwait(false);

            return OperationResult<SshHostTrustApplyResult>.Fail(
                "known_hosts was written, but the trusted host keys could not be verified. The change was rolled back.",
                rollbackMessage);
        }

        return OperationResult<SshHostTrustApplyResult>.Ok(new SshHostTrustApplyResult
        {
            KnownHostsPath = refreshed.KnownHostsPath,
            BackupPath = backupPath,
            AddedKeyCount = refreshed.ScannedKeys.Count,
            TrustedKeys = refreshed.ScannedKeys
        }, "SSH server host keys were trusted.");
    }

    private async Task<string> RollbackKnownHostsAsync(string knownHostsPath, string? backupPath)
    {
        try
        {
            if (backupPath is not null)
            {
                var backupBytes = await _fileSystem.ReadAllBytesAsync(
                    backupPath,
                    CancellationToken.None).ConfigureAwait(false);
                await _fileSystem.WriteAllBytesAtomicAsync(
                    knownHostsPath,
                    backupBytes,
                    CancellationToken.None).ConfigureAwait(false);
                return $"The original known_hosts file was restored from {backupPath}.";
            }

            if (_fileSystem.FileExists(knownHostsPath))
            {
                _fileSystem.DeleteFile(knownHostsPath);
            }

            return "The newly created known_hosts file was removed.";
        }
        catch (Exception exception)
        {
            return $"Rollback failed: {exception.Message}";
        }
    }

    public static bool IsHostKeyVerificationFailure(ProcessResult process)
        => (process.StandardOutput + "\n" + process.StandardError)
            .Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase);

    private async Task<OperationResult<SshKnownHostsFileVersion>> ReadFileVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(path))
        {
            return OperationResult<SshKnownHostsFileVersion>.Ok(new(false, string.Empty));
        }

        if ((_fileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return OperationResult<SshKnownHostsFileVersion>.Fail(
                "The known_hosts file is a reparse point and will not be modified automatically.");
        }

        var bytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaximumKnownHostsBytes)
        {
            return OperationResult<SshKnownHostsFileVersion>.Fail(
                $"The known_hosts file exceeds the {MaximumKnownHostsBytes / 1024 / 1024} MiB safety limit.");
        }

        return OperationResult<SshKnownHostsFileVersion>.Ok(new(
            true,
            Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private string? ResolveSiblingExecutable(ToolchainInfo tools, string executableName)
    {
        var candidates = new[] { tools.Ssh.SelectedPath, tools.SshKeygen.SelectedPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(Path.GetDirectoryName(path!)!, executableName))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(_fileSystem.FileExists);
    }

    private static IReadOnlyList<SshHostPublicKey> ParseScannedKeys(string output, string expectedHostIdentifier)
    {
        var result = new List<SshHostPublicKey>();
        foreach (var line in SplitLines(output))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !HostFieldMatches(parts[0], expectedHostIdentifier)
                || !TryCreateKey(parts[1], parts[2], out var key))
            {
                continue;
            }

            if (!result.Contains(key))
            {
                result.Add(key);
                if (result.Count >= MaximumScannedKeys)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<SshHostPublicKey> ParseExistingKeys(string output, out bool containsMarkers)
    {
        containsMarkers = false;
        var result = new List<SshHostPublicKey>();
        foreach (var line in SplitLines(output))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('@'))
            {
                containsMarkers = true;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index + 1 < parts.Length; index++)
            {
                if (!TryCreateKey(parts[index], parts[index + 1], out var key))
                {
                    continue;
                }

                if (!result.Contains(key))
                {
                    result.Add(key);
                }

                break;
            }
        }

        return result;
    }

    private static bool TryCreateKey(string keyType, string keyData, out SshHostPublicKey key)
    {
        key = null!;
        var publicKey = $"{keyType} {keyData}";
        if (!SshKeyFormatDetector.TryNormalizeOpenSshPublicKey(publicKey, out _, out var normalizedType)
            || !SshKeyFormatDetector.TryGetSha256Fingerprint(publicKey, out var fingerprint))
        {
            return false;
        }

        key = new SshHostPublicKey(normalizedType, keyData, fingerprint);
        return true;
    }

    private static SshHostTrustStatus DetermineStatus(
        IReadOnlyList<SshHostPublicKey> scanned,
        IReadOnlyList<SshHostPublicKey> existing)
    {
        if (existing.Count == 0)
        {
            return SshHostTrustStatus.NotTrusted;
        }

        return existing.All(scanned.Contains) && existing.Any(scanned.Contains)
            ? SshHostTrustStatus.Trusted
            : SshHostTrustStatus.Conflict;
    }

    private static bool KeySetsEqual(
        IReadOnlyList<SshHostPublicKey> left,
        IReadOnlyList<SshHostPublicKey> right)
        => left.Count == right.Count && left.All(right.Contains);

    private static bool EndpointMatches(GitServiceInstance service, SshHostTrustPreview preview)
        => string.Equals(service.Id, preview.ServiceInstanceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(service.HostName.Trim(), preview.HostName, StringComparison.OrdinalIgnoreCase)
            && (service.SshPort ?? 22) == preview.Port;

    private static string FormatHostIdentifier(string hostName, int port)
        => port == 22 ? hostName : $"[{hostName}]:{port}";

    private static bool HostFieldMatches(string hostField, string expected)
        => hostField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => string.Equals(item, expected, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitLines(string text)
        => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool EndsWithNewLine(StringBuilder text)
        => text.Length > 0 && text[^1] is '\r' or '\n';
}
