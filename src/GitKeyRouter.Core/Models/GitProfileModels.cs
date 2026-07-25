namespace GitKeyRouter.Core.Models;

public enum GitProfileRuleKind
{
    Directory,
    RemoteUrl
}

public sealed class GitProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string UserEmail { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    public bool EnableCommitSigning { get; set; }

    public string DefaultServiceInstanceId { get; set; } = string.Empty;

    public string DefaultIdentityId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GitProfileRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ProfileId { get; set; } = string.Empty;

    public GitProfileRuleKind Kind { get; set; }

    public string Pattern { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

public sealed class GitProfileConfigPreview
{
    public required string MasterConfigPath { get; init; }

    public required string MasterConfigText { get; init; }

    public required IReadOnlyDictionary<string, FileContentSnapshot> OriginalFiles { get; init; }

    public required IReadOnlyDictionary<string, string> ProfileFiles { get; init; }

    public required string DiffText { get; init; }

    public bool HasChanges { get; init; }
}

public sealed class GitProfileApplyResult
{
    public required string MasterConfigPath { get; init; }

    public int ProfileFileCount { get; init; }

    public ProcessResult? IncludeRegistrationResult { get; init; }
}
