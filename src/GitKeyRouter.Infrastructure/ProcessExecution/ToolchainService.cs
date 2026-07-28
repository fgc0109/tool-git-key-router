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
        IEnumerable<string> candidates,
        IReadOnlyList<string> versionArguments,
        CancellationToken cancellationToken,
        bool preferFileVersion = false)
    {
        var existing = candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (existing.Count == 0)
        {
            return new ExecutableInfo { Name = name, Exists = false };
        }

        var selected = existing[0];
        var result = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = selected,
            Arguments = versionArguments,
            Timeout = TimeSpan.FromSeconds(10)
        }, cancellationToken).ConfigureAwait(false);
        var output = FirstNonEmptyLine(result.StandardOutput, result.StandardError);
        var fileVersion = TryGetFileVersion(selected);
        var version = preferFileVersion && !string.IsNullOrWhiteSpace(fileVersion)
            ? fileVersion
            : !string.IsNullOrWhiteSpace(output) ? output : fileVersion;

        return new ExecutableInfo
        {
            Name = name,
            Exists = true,
            SelectedPath = selected,
            CandidatePaths = existing,
            Version = version,
            ProbeResult = result
        };
    }

    private static IEnumerable<string> GitCandidates()
    {
        foreach (var path in PathCandidates("git.exe"))
        {
            yield return path;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
        yield return Path.Combine(programFiles, "Git", "bin", "git.exe");
        yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");
    }

    private static IEnumerable<string> SshCandidates(string executableName)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Path.Combine(windows, "System32", "OpenSSH", executableName);

        foreach (var path in PathCandidates(executableName))
        {
            yield return path;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Git", "usr", "bin", executableName);
    }

    private static IEnumerable<string> WingetCandidates()
    {
        foreach (var path in PathCandidates("winget.exe"))
        {
            yield return path;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
    }

    private static IEnumerable<string> GitHubCliCandidates()
    {
        foreach (var path in PathCandidates("gh.exe"))
        {
            yield return path;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(programFiles, "GitHub CLI", "gh.exe");
        yield return Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe");
        yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "gh.exe");
    }

    private static IEnumerable<string> PathCandidates(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory.Trim('"'), executableName);
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
}
