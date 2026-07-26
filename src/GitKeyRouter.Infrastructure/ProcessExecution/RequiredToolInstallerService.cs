using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Infrastructure.ProcessExecution;

public sealed class RequiredToolInstallerService : IRequiredToolInstallerService
{
    private const string GitManualUri = "https://git-scm.com/install/windows";
    private const string OpenSshManualUri = "ms-settings:optionalfeatures";
    private const string OpenSshCapabilityName = "OpenSSH.Client~~~~0.0.1.0";

    private readonly IToolchainService _toolchainService;
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _powerShellLocator;

    public RequiredToolInstallerService(
        IToolchainService toolchainService,
        IProcessRunner processRunner,
        Func<string?>? powerShellLocator = null)
    {
        _toolchainService = toolchainService;
        _processRunner = processRunner;
        _powerShellLocator = powerShellLocator ?? FindPowerShell;
    }

    public async Task<RequiredToolInstallPlan> BuildPlanAsync(CancellationToken cancellationToken = default)
    {
        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        return BuildPlan(tools);
    }

    public async Task<OperationResult<RequiredToolInstallResult>> InstallMissingAsync(
        CancellationToken cancellationToken = default)
    {
        var before = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        var steps = new List<RequiredToolInstallStep>();
        var current = before;

        if (!current.Git.Exists)
        {
            if (!current.Winget.Exists || string.IsNullOrWhiteSpace(current.Winget.SelectedPath))
            {
                return OperationResult<RequiredToolInstallResult>.Fail(
                    "Git cannot be installed automatically because Windows Package Manager was not found.",
                    $"Open the official Git for Windows page: {GitManualUri}");
            }

            var gitResult = await _processRunner.RunAsync(new ProcessRequest
            {
                ExecutablePath = current.Winget.SelectedPath,
                Arguments =
                [
                    "install",
                    "--id", "Git.Git",
                    "--exact",
                    "--source", "winget",
                    "--silent",
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                    "--disable-interactivity"
                ],
                Timeout = TimeSpan.FromMinutes(15),
                CreateNoWindow = false
            }, cancellationToken).ConfigureAwait(false);
            var gitSucceeded = IsSuccessful(gitResult);
            steps.Add(new RequiredToolInstallStep
            {
                Kind = RequiredToolKind.Git,
                DisplayName = "Git for Windows",
                Success = gitSucceeded,
                Message = gitSucceeded ? "Git installer completed." : "Git installation failed.",
                ProcessResult = gitResult
            });
            if (!gitSucceeded)
            {
                return FailedInstallation("Git for Windows installation failed.", gitResult);
            }

            current = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Git.Exists)
            {
                return OperationResult<RequiredToolInstallResult>.Fail(
                    "Git installation completed, but git.exe is still not detectable.",
                    "Restart GitKeyRouter or Windows, then run the software check again.");
            }
        }

        if (!current.Ssh.Exists || !current.SshKeygen.Exists)
        {
            var powerShellPath = _powerShellLocator();
            if (string.IsNullOrWhiteSpace(powerShellPath))
            {
                return OperationResult<RequiredToolInstallResult>.Fail(
                    "OpenSSH Client cannot be installed automatically because Windows PowerShell was not found.",
                    "Open Windows Settings > Optional features and install OpenSSH Client.");
            }

            var openSshResult = await _processRunner.RunAsync(new ProcessRequest
            {
                ExecutablePath = powerShellPath,
                Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", CreateOpenSshInstallScript()],
                Timeout = TimeSpan.FromMinutes(15),
                CreateNoWindow = false
            }, cancellationToken).ConfigureAwait(false);
            var openSshSucceeded = IsSuccessful(openSshResult);
            steps.Add(new RequiredToolInstallStep
            {
                Kind = RequiredToolKind.OpenSshClient,
                DisplayName = "Windows OpenSSH Client",
                Success = openSshSucceeded,
                Message = openSshSucceeded ? "OpenSSH Client installation completed." : "OpenSSH Client installation failed.",
                ProcessResult = openSshResult
            });
            if (!openSshSucceeded)
            {
                return FailedInstallation("Windows OpenSSH Client installation failed or the UAC prompt was cancelled.", openSshResult);
            }

            current = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = new RequiredToolInstallResult
        {
            Before = before,
            After = current,
            Steps = steps
        };
        if (!result.AllRequiredToolsAvailable)
        {
            return OperationResult<RequiredToolInstallResult>.Fail(
                "Installation finished, but one or more required tools are still unavailable.",
                DescribeMissing(current),
                "Restart GitKeyRouter or Windows and run the software check again.");
        }

        return OperationResult<RequiredToolInstallResult>.Ok(result, "All required tools are installed and detectable.");
    }

    private RequiredToolInstallPlan BuildPlan(ToolchainInfo tools)
    {
        var items = new List<RequiredToolInstallItem>();
        if (!tools.Git.Exists)
        {
            items.Add(new RequiredToolInstallItem
            {
                Kind = RequiredToolKind.Git,
                DisplayName = "Git for Windows",
                Reason = "Git URL rewrite, repository tests and global Git configuration require git.exe.",
                InstallMethod = tools.Winget.Exists
                    ? "Download and install Git.Git through Windows Package Manager."
                    : "Windows Package Manager is unavailable; use the official Git download page.",
                ManualInstallUri = GitManualUri,
                CanInstallAutomatically = tools.Winget.Exists && !string.IsNullOrWhiteSpace(tools.Winget.SelectedPath)
            });
        }

        if (!tools.Ssh.Exists || !tools.SshKeygen.Exists)
        {
            var missing = new List<string>();
            if (!tools.Ssh.Exists)
            {
                missing.Add("ssh.exe");
            }

            if (!tools.SshKeygen.Exists)
            {
                missing.Add("ssh-keygen.exe");
            }

            var powerShellAvailable = !string.IsNullOrWhiteSpace(_powerShellLocator());
            items.Add(new RequiredToolInstallItem
            {
                Kind = RequiredToolKind.OpenSshClient,
                DisplayName = "Windows OpenSSH Client",
                Reason = $"Missing: {string.Join(", ", missing)}. SSH connection tests and key management require these tools.",
                InstallMethod = powerShellAvailable
                    ? "Install the Windows OpenSSH Client optional capability. A UAC confirmation will be shown."
                    : "Windows PowerShell is unavailable; install OpenSSH Client from Windows Optional features.",
                ManualInstallUri = OpenSshManualUri,
                CanInstallAutomatically = powerShellAvailable
            });
        }

        return new RequiredToolInstallPlan
        {
            Toolchain = tools,
            MissingTools = items
        };
    }

    private static OperationResult<RequiredToolInstallResult> FailedInstallation(string message, ProcessResult process)
    {
        var errors = new List<string>();
        if (process.StartException is not null)
        {
            errors.Add(process.StartException.Message);
        }

        if (!string.IsNullOrWhiteSpace(process.StandardError))
        {
            errors.Add(process.StandardError);
        }

        if (process.TimedOut)
        {
            errors.Add("The installer timed out.");
        }

        if (process.Cancelled)
        {
            errors.Add("The installer was cancelled.");
        }

        if (!string.IsNullOrWhiteSpace(process.TerminationError))
        {
            errors.Add(process.TerminationError);
        }

        errors.Add($"Exit code: {process.ExitCode?.ToString() ?? "<none>"}");
        return OperationResult<RequiredToolInstallResult>.Fail(message, errors.ToArray());
    }

    private static bool IsSuccessful(ProcessResult result)
        => result.StartException is null && !result.TimedOut && !result.Cancelled && result.ExitCode == 0;

    private static string DescribeMissing(ToolchainInfo tools)
    {
        var missing = new List<string>();
        if (!tools.Git.Exists)
        {
            missing.Add("git.exe");
        }

        if (!tools.Ssh.Exists)
        {
            missing.Add("ssh.exe");
        }

        if (!tools.SshKeygen.Exists)
        {
            missing.Add("ssh-keygen.exe");
        }

        return $"Still missing: {string.Join(", ", missing)}";
    }

    private static string CreateOpenSshInstallScript()
        => "$command = '$ErrorActionPreference = ''Stop''; Add-WindowsCapability -Online -Name "
           + OpenSshCapabilityName
           + " | Out-Host'; "
           + "$process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList "
           + "@('-NoProfile','-ExecutionPolicy','Bypass','-Command',$command) -Wait -PassThru; "
           + "exit $process.ExitCode";

    private static string? FindPowerShell()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<string>
        {
            Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "powershell.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
}
