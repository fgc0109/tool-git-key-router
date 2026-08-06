namespace GitKeyRouter.Core.Models;

public enum SshHostTrustStatus
{
    NotTrusted,
    Trusted,
    Conflict
}

public sealed record SshHostPublicKey(
    string KeyType,
    string KeyData,
    string Fingerprint);

public sealed record SshKnownHostsFileVersion(
    bool Exists,
    string Sha256);

public sealed class SshHostTrustPreview
{
    public required string ServiceInstanceId { get; init; }

    public required string ServiceDisplayName { get; init; }

    public required string HostName { get; init; }

    public required int Port { get; init; }

    public required string HostIdentifier { get; init; }

    public required string KnownHostsPath { get; init; }

    public required SshHostTrustStatus Status { get; init; }

    public required SshKnownHostsFileVersion FileVersion { get; init; }

    public required IReadOnlyList<SshHostPublicKey> ScannedKeys { get; init; }

    public required IReadOnlyList<SshHostPublicKey> ExistingKeys { get; init; }

    public bool ExistingEntriesContainMarkers { get; init; }

    public required ProcessResult ScanProcess { get; init; }

    public bool CanTrust => Status == SshHostTrustStatus.NotTrusted;
}

public sealed class SshHostTrustApplyResult
{
    public required string KnownHostsPath { get; init; }

    public string? BackupPath { get; init; }

    public int AddedKeyCount { get; init; }

    public required IReadOnlyList<SshHostPublicKey> TrustedKeys { get; init; }
}
