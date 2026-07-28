namespace GitKeyRouter.Core.Models;

public sealed class PortableBackupPreview
{
    public DateTimeOffset CreatedAt { get; init; }

    public int ApplicationSchemaVersion { get; init; }

    public int IdentityCount { get; init; }

    public int KeyFileCount { get; init; }

    public int GitRewriteCount { get; init; }

    public bool HasSshConfig { get; init; }

    public long PackageBytes { get; init; }

    public string Summary =>
        $"Created {CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}; "
        + $"{IdentityCount} identities; {KeyFileCount} key files; "
        + $"{GitRewriteCount} Git URL rewrites; SSH config: {(HasSshConfig ? "included" : "not present")}.";
}

public sealed class PortableBackupImportResult
{
    public required string ManagedKeyDirectory { get; init; }

    public int IdentityCount { get; init; }

    public int KeyFileCount { get; init; }

    public int GitRewriteCount { get; init; }

    public bool RequiresApplicationRefresh { get; init; } = true;
}
