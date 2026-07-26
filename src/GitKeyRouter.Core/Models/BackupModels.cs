namespace GitKeyRouter.Core.Models;

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 2;

    public DateTimeOffset CreatedAt { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string BackupDirectory { get; set; } = string.Empty;

    public string? ApplicationVersion { get; set; }

    public bool AppConfigExisted { get; set; }

    public int? AppConfigSchemaVersion { get; set; }

    public bool SshConfigExisted { get; set; }

    public int GitRewriteCount { get; set; }

    public string? GitRewriteCaptureError { get; set; }

    public Dictionary<string, BackupFileIntegrity> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BackupFileIntegrity
{
    public long Length { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

public sealed class BackupSnapshot
{
    public required BackupManifest Manifest { get; init; }

    public string? AppConfigText { get; init; }

    public string? SshConfigText { get; init; }

    public IReadOnlyList<GitUrlRewriteRule> GitUrlRewrites { get; init; } = [];
}

public enum BackupHealthStatus
{
    Complete,
    Pending,
    Damaged,
    Unsupported,
    Unknown
}

public sealed class BackupInventoryItem
{
    public required string BackupDirectory { get; init; }

    public required BackupHealthStatus Status { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }

    public bool CanClean { get; init; }

    public BackupManifest? Manifest { get; init; }

    public IReadOnlyList<string> Details { get; init; } = [];
}

public sealed class BackupCleanupPlan
{
    public List<BackupCleanupTarget> Targets { get; init; } = [];

    public List<string> Rejected { get; init; } = [];

    public bool HasTargets => Targets.Count > 0;
}

public sealed record BackupCleanupTarget(
    string BackupDirectory,
    BackupHealthStatus Status,
    DateTimeOffset LastWriteTimeUtc,
    string Reason);
