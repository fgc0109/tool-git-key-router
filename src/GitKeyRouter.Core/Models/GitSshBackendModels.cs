namespace GitKeyRouter.Core.Models;

public enum GitSshBackendKind
{
    OpenSsh,
    PuttyPlink,
    TortoisePlink,
    Unknown
}

public sealed class GitSshBackendInspection
{
    public required GitSshBackendKind Kind { get; init; }

    public required string DisplayName { get; init; }

    public required string Source { get; init; }

    public string? EffectiveCommand { get; init; }

    public string? EffectiveExecutable { get; init; }

    public string? EffectiveVariant { get; init; }

    public string? CommandOrigin { get; init; }

    public string? VariantOrigin { get; init; }

    public string? SelectedOpenSshPath { get; init; }

    public IReadOnlyList<string> EnvironmentBlockers { get; init; } = [];

    public bool IsOpenSsh => Kind == GitSshBackendKind.OpenSsh;

    public bool CanApplyOpenSshFix => !IsOpenSsh
        && Kind is GitSshBackendKind.PuttyPlink or GitSshBackendKind.TortoisePlink
        && EnvironmentBlockers.Count == 0
        && !string.IsNullOrWhiteSpace(SelectedOpenSshPath);
}

public sealed class GitSshBackendApplyResult
{
    public required GitSshBackendInspection Before { get; init; }

    public required GitSshBackendInspection After { get; init; }

    public required string CoreSshCommand { get; init; }

    public required string SshVariant { get; init; }
}
