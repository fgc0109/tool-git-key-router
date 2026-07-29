using System.Diagnostics;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Infrastructure.ProcessExecution;

public sealed class ToolchainService : IToolchainService
{
    private readonly IProcessRunner _processRunner;

    public ToolchainService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<ToolchainInfo> InspectAsync(CancellationToken cancellationToken = default)
    {
        var gitTask = InspectExecutableAsync("git.exe", GitCandidates(), ["--version"], cancellationToken);
        var sshTask = InspectExecutableAsync("ssh.exe", SshCandidates("ssh.exe"), ["-V"], cancellationToken);
        var keygenTask = InspectExecutableAsync("ssh-keygen.exe", SshCandidates("ssh-keygen.exe"), ["-?"], cancellationToken, preferFileVersion: true);
        var wingetTask = InspectExecutableAsync("winget.exe", WingetCandidates(), ["--version"], cancellationToken);
        var ghTask = InspectExecutableAsync("gh.exe", GitHubCliCandidates(), ["--version"], cancellationToken);
        await Task.WhenAll(gitTask, sshTask, keygenTask, wingetTask, ghTask).ConfigureAwait(false);
        return new ToolchainInfo
        {
            Git = await gitTask.ConfigureAwait(false),
            Ssh = await sshTask.ConfigureAwait(false),
            SshKeygen = await keygenTask.ConfigureAwait(false),
            Winget = await wingetTask.ConfigureAwait(false),
            Gh = await ghTask.ConfigureAwait(false)
        };
    }

    private async Task<ExecutableInfo> InspectExecutableAsync(
        string name,
        IEnumerable<ToolCandidate> candidates,
        IReadOnlyList<string> versionArguments,
        CancellationToken cancellationToken,
        bool preferFileVersion = false)
    {
        var existing = candidates
            .Where(candidate => File.Exists(candidate.Path))
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (existing.Count == 0)
        {
            return new ExecutableInfo { Name = name, Exists = false };
        }

        var selected = existing[0];
        var result = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = selected.Path,
            Arguments = versionArguments,
            Timeout = TimeSpan.FromSeconds(10)
        }, cancellationToken).ConfigureAwait(false);
        var output = FirstNonEmptyLine(result.StandardOutput, result.StandardError);
        var fileVersion = TryGetFileVersion(selected.Path);
        var version = preferFileVersion && !string.IsNullOrWhiteSpace(fileVersion)
            ? fileVersion
            : !string.IsNullOrWhiteSpace(output) ? output : fileVersion;

        return new ExecutableInfo
        {
            Name = name,
            Exists = true,
            SelectedPath = selected.Path,
            SelectedSource = selected.Source,
            CandidatePaths = existing.Select(candidate => candidate.Path).ToList(),
            Version = version,
            ProbeResult = result
        };
    }

    private static IEnumerable<ToolCandidate> GitCandidates()
    {
        foreach (var path in PathCandidates("git.exe"))
        {
            yield return path;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return new ToolCandidate(Path.Combine(programFiles, "Git", "cmd", "git.exe"), "Program Files");
        yield return new ToolCandidate(Path.Combine(programFiles, "Git", "bin", "git.exe"), "Program Files");
        yield return new ToolCandidate(Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"), "Local AppData");
    }

    private static IEnumerable<ToolCandidate> SshCandidates(string executableName)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return new ToolCandidate(Path.Combine(windows, "System32", "OpenSSH", executableName), "Windows OpenSSH");

        foreach (var path in PathCandidates(executableName))
        {
            yield return path;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return new ToolCandidate(Path.Combine(programFiles, "Git", "usr", "bin", executableName), "Git for Windows");
    }

    private static IEnumerable<ToolCandidate> WingetCandidates()
    {
        foreach (var path in PathCandidates("winget.exe"))
        {
            yield return path;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return new ToolCandidate(Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe"), "Windows Apps");
    }

    private static IEnumerable<ToolCandidate> GitHubCliCandidates()
    {
        foreach (var path in PathCandidates("gh.exe"))
        {
            yield return path;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return new ToolCandidate(Path.Combine(programFiles, "GitHub CLI", "gh.exe"), "Program Files");
        yield return new ToolCandidate(Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe"), "Local AppData");
        yield return new ToolCandidate(Path.Combine(localAppData, "Microsoft", "WindowsApps", "gh.exe"), "Windows Apps");
    }

    private static IEnumerable<ToolCandidate> PathCandidates(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return new ToolCandidate(Path.Combine(directory.Trim('"'), executableName), "PATH");
        }
    }

    private static string? TryGetFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmptyLine(params string[] values)
        => values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault();

    private sealed record ToolCandidate(string Path, string Source);
}
