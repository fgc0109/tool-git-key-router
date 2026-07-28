namespace GitKeyRouter.Core.Models;

public sealed class ExecutableInfo
{
    public required string Name { get; init; }

    public bool Exists { get; init; }

    public string? SelectedPath { get; init; }

    public IReadOnlyList<string> CandidatePaths { get; init; } = [];

    public string? Version { get; init; }

    public ProcessResult? ProbeResult { get; init; }
}

public sealed class ToolchainInfo
{
    public required ExecutableInfo Git { get; init; }

    public required ExecutableInfo Ssh { get; init; }

    public required ExecutableInfo SshKeygen { get; init; }

    public ExecutableInfo Winget { get; init; } = new() { Name = "winget.exe", Exists = false };

    public ExecutableInfo Gh { get; init; } = new() { Name = "gh.exe", Exists = false };
}
