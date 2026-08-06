using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Services;

public sealed class GitSshBackendService
{
    private static readonly string[] RelevantEnvironmentVariables =
    [
        "GIT_SSH_COMMAND",
        "GIT_SSH",
        "GIT_SSH_VARIANT"
    ];

    private readonly IProcessRunner _processRunner;
    private readonly IToolchainService _toolchainService;
    private readonly IReadOnlyDictionary<string, string?> _environmentVariables;

    public GitSshBackendService(
        IProcessRunner processRunner,
        IToolchainService toolchainService,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        _processRunner = processRunner;
        _toolchainService = toolchainService;
        _environmentVariables = environmentVariables ?? new Dictionary<string, string?>();
    }

    public async Task<OperationResult<GitSshBackendInspection>> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.Git.Exists || string.IsNullOrWhiteSpace(tools.Git.SelectedPath))
        {
            return OperationResult<GitSshBackendInspection>.Fail("git.exe was not found.");
        }

        var commandConfig = await ReadConfigAsync(
            tools.Git.SelectedPath,
            "core.sshCommand",
            cancellationToken).ConfigureAwait(false);
        if (!commandConfig.Success)
        {
            return OperationResult<GitSshBackendInspection>.Fail(
                commandConfig.Message,
                commandConfig.Errors.ToArray());
        }

        var variantConfig = await ReadConfigAsync(
            tools.Git.SelectedPath,
            "ssh.variant",
            cancellationToken).ConfigureAwait(false);
        if (!variantConfig.Success)
        {
            return OperationResult<GitSshBackendInspection>.Fail(
                variantConfig.Message,
                variantConfig.Errors.ToArray());
        }

        var environmentCommand = GetEnvironmentValue("GIT_SSH_COMMAND");
        var environmentSsh = GetEnvironmentValue("GIT_SSH");
        var environmentVariant = GetEnvironmentValue("GIT_SSH_VARIANT");
        var effectiveCommand = FirstNonEmpty(environmentCommand, commandConfig.Value?.Value, environmentSsh);
        var effectiveVariant = FirstNonEmpty(environmentVariant, variantConfig.Value?.Value);
        string? probedCommand = null;
        if (string.IsNullOrWhiteSpace(effectiveCommand)
            && (string.IsNullOrWhiteSpace(effectiveVariant)
                || string.Equals(effectiveVariant.Trim(), "auto", StringComparison.OrdinalIgnoreCase)))
        {
            probedCommand = await ProbeDefaultCommandAsync(
                tools.Git.SelectedPath,
                cancellationToken).ConfigureAwait(false);
            effectiveCommand = probedCommand;
        }

        var kind = Classify(effectiveCommand, effectiveVariant);
        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(environmentCommand))
        {
            sources.Add("environment variable GIT_SSH_COMMAND");
        }
        else if (!string.IsNullOrWhiteSpace(commandConfig.Value?.Value))
        {
            sources.Add($"Git config core.sshCommand ({commandConfig.Value.Origin ?? "unknown origin"})");
        }
        else if (!string.IsNullOrWhiteSpace(environmentSsh))
        {
            sources.Add("environment variable GIT_SSH");
        }
        else if (!string.IsNullOrWhiteSpace(probedCommand))
        {
            sources.Add("Git local trace probe");
        }
        else
        {
            sources.Add("Git default SSH backend");
        }

        if (!string.IsNullOrWhiteSpace(environmentVariant))
        {
            sources.Add("environment variable GIT_SSH_VARIANT");
        }
        else if (!string.IsNullOrWhiteSpace(variantConfig.Value?.Value))
        {
            sources.Add($"Git config ssh.variant ({variantConfig.Value.Origin ?? "unknown origin"})");
        }

        var source = string.Join(" + ", sources);

        if (string.IsNullOrWhiteSpace(effectiveCommand)
            && string.IsNullOrWhiteSpace(effectiveVariant))
        {
            kind = GitSshBackendKind.OpenSsh;
        }

        var blockers = RelevantEnvironmentVariables
            .Select(name => (Name: name, Value: GetEnvironmentValue(name)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value)
                && Classify(
                    item.Name == "GIT_SSH_VARIANT" ? null : item.Value,
                    item.Name == "GIT_SSH_VARIANT" ? item.Value : null) != GitSshBackendKind.OpenSsh)
            .Select(item => item.Name)
            .ToList();

        return OperationResult<GitSshBackendInspection>.Ok(new GitSshBackendInspection
        {
            Kind = kind,
            DisplayName = DisplayName(kind),
            Source = source,
            EffectiveCommand = effectiveCommand,
            EffectiveExecutable = ExtractExecutableName(effectiveCommand),
            EffectiveVariant = effectiveVariant,
            CommandOrigin = commandConfig.Value?.Origin,
            VariantOrigin = variantConfig.Value?.Origin,
            SelectedOpenSshPath = IsCompatibleOpenSshCandidate(tools.Ssh) ? tools.Ssh.SelectedPath : null,
            EnvironmentBlockers = blockers
        }, "Git SSH backend inspected.");
    }

    private async Task<string?> ProbeDefaultCommandAsync(
        string gitPath,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>(_environmentVariables, StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TRACE"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ASKPASS"] = null,
            ["SSH_ASKPASS"] = null
        };
        var result = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = gitPath,
            Arguments =
            [
                "ls-remote",
                "ssh://git@127.0.0.1:1/__gitkeyrouter_backend_probe__.git",
                "HEAD"
            ],
            Timeout = TimeSpan.FromSeconds(5),
            MaxOutputLines = 128,
            MaxOutputCharactersPerLine = 16 * 1024,
            EnvironmentVariables = environment
        }, cancellationToken).ConfigureAwait(false);
        if (result.StartException is not null || result.TimedOut || result.Cancelled)
        {
            return null;
        }

        foreach (var line in result.StandardError.Split(
                     ["\r\n", "\n", "\r"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string marker = "trace: start_command:";
            var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var command = line[(markerIndex + marker.Length)..].Trim();
            var shellPrefix = command.LastIndexOf(';');
            return shellPrefix >= 0 ? command[(shellPrefix + 1)..].Trim() : command;
        }

        return null;
    }

    public async Task<OperationResult<GitSshBackendApplyResult>> UseOpenSshAsync(
        GitSshBackendInspection expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var currentResult = await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!currentResult.Success || currentResult.Value is null)
        {
            return OperationResult<GitSshBackendApplyResult>.Fail(
                currentResult.Message,
                currentResult.Errors.ToArray());
        }

        var current = currentResult.Value;
        if (!Equivalent(expected, current))
        {
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "The Git SSH backend changed after the preview. Review the current settings again.");
        }

        if (current.IsOpenSsh)
        {
            return OperationResult<GitSshBackendApplyResult>.Fail("Git already uses OpenSSH; no setting was changed.");
        }

        if (current.EnvironmentBlockers.Count > 0)
        {
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "Git SSH environment overrides prevent a persistent Git configuration fix.",
                current.EnvironmentBlockers.ToArray());
        }

        if (string.IsNullOrWhiteSpace(current.SelectedOpenSshPath))
        {
            return OperationResult<GitSshBackendApplyResult>.Fail("OpenSSH ssh.exe was not found.");
        }

        if (!current.CanApplyOpenSshFix)
        {
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "The current custom SSH backend is unknown and will not be replaced automatically.");
        }

        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.Git.Exists || string.IsNullOrWhiteSpace(tools.Git.SelectedPath))
        {
            return OperationResult<GitSshBackendApplyResult>.Fail("git.exe was not found.");
        }

        var previousCommand = await ReadGlobalValuesAsync(
            tools.Git.SelectedPath,
            "core.sshCommand",
            cancellationToken).ConfigureAwait(false);
        var previousVariant = await ReadGlobalValuesAsync(
            tools.Git.SelectedPath,
            "ssh.variant",
            cancellationToken).ConfigureAwait(false);
        if (!previousCommand.Success || !previousVariant.Success)
        {
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "Unable to capture the current global Git SSH settings before changing them.",
                previousCommand.Errors.Concat(previousVariant.Errors).ToArray());
        }

        var sshCommand = QuoteGitCommandPath(current.SelectedOpenSshPath);
        var setCommand = await RunGitAsync(
            tools.Git.SelectedPath,
            ["config", "--global", "--replace-all", "core.sshCommand", sshCommand],
            cancellationToken).ConfigureAwait(false);
        if (!setCommand.Succeeded)
        {
            var rollback = await RestoreGlobalValuesAsync(
                tools.Git.SelectedPath,
                "core.sshCommand",
                previousCommand.Value ?? [],
                CancellationToken.None).ConfigureAwait(false);
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "Unable to set global core.sshCommand to OpenSSH. The previous value was restored.",
                setCommand.StandardError,
                rollback.Success ? string.Empty : $"Rollback failed: {string.Join("; ", rollback.Errors)}");
        }

        var setVariant = await RunGitAsync(
            tools.Git.SelectedPath,
            ["config", "--global", "--replace-all", "ssh.variant", "ssh"],
            cancellationToken).ConfigureAwait(false);
        if (!setVariant.Succeeded)
        {
            var rollbackCommand = await RestoreGlobalValuesAsync(
                tools.Git.SelectedPath,
                "core.sshCommand",
                previousCommand.Value ?? [],
                CancellationToken.None).ConfigureAwait(false);
            var rollbackVariant = await RestoreGlobalValuesAsync(
                tools.Git.SelectedPath,
                "ssh.variant",
                previousVariant.Value ?? [],
                CancellationToken.None).ConfigureAwait(false);
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "Unable to set global ssh.variant to OpenSSH. The previous global settings were restored.",
                setVariant.StandardError,
                rollbackCommand.Success && rollbackVariant.Success
                    ? string.Empty
                    : $"Rollback failed: {string.Join("; ", rollbackCommand.Errors.Concat(rollbackVariant.Errors))}");
        }

        OperationResult<GitSshBackendInspection> afterResult;
        try
        {
            afterResult = await InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            afterResult = OperationResult<GitSshBackendInspection>.Fail(
                "Unable to verify the updated Git SSH backend.",
                exception.Message);
        }

        if (!afterResult.Success || afterResult.Value is null || !afterResult.Value.IsOpenSsh)
        {
            var rollbackCommand = await RestoreGlobalValuesAsync(
                tools.Git.SelectedPath,
                "core.sshCommand",
                previousCommand.Value ?? [],
                CancellationToken.None).ConfigureAwait(false);
            var rollbackVariant = await RestoreGlobalValuesAsync(
                tools.Git.SelectedPath,
                "ssh.variant",
                previousVariant.Value ?? [],
                CancellationToken.None).ConfigureAwait(false);
            return OperationResult<GitSshBackendApplyResult>.Fail(
                "Git did not resolve to OpenSSH after applying the settings. The previous global settings were restored.",
                rollbackCommand.Success && rollbackVariant.Success
                    ? string.Empty
                    : "At least one rollback operation failed; inspect the global Git configuration.");
        }

        return OperationResult<GitSshBackendApplyResult>.Ok(new GitSshBackendApplyResult
        {
            Before = current,
            After = afterResult.Value,
            CoreSshCommand = sshCommand,
            SshVariant = "ssh"
        }, "Git now uses OpenSSH.");
    }

    private async Task<OperationResult<GitConfigValue?>> ReadConfigAsync(
        string gitPath,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            gitPath,
            ["config", "--show-origin", "--get", key],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return OperationResult<GitConfigValue?>.Ok(null);
        }

        if (!result.Succeeded)
        {
            return OperationResult<GitConfigValue?>.Fail(
                $"Unable to read Git setting '{key}'.",
                result.StandardError);
        }

        return OperationResult<GitConfigValue?>.Ok(ParseConfigValue(result.StandardOutput));
    }

    private async Task<OperationResult<IReadOnlyList<string>>> ReadGlobalValuesAsync(
        string gitPath,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            gitPath,
            ["config", "--global", "--get-all", key],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return OperationResult<IReadOnlyList<string>>.Ok([]);
        }

        if (!result.Succeeded)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(
                $"Unable to read global Git setting '{key}'.",
                result.StandardError);
        }

        return OperationResult<IReadOnlyList<string>>.Ok(result.StandardOutput
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .ToList());
    }

    private async Task<OperationResult> RestoreGlobalValuesAsync(
        string gitPath,
        string key,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var remove = await RunGitAsync(
            gitPath,
            ["config", "--global", "--unset-all", key],
            cancellationToken).ConfigureAwait(false);
        if (!remove.Succeeded && remove.ExitCode != 5)
        {
            return OperationResult.Fail($"Unable to clear global Git setting '{key}'.", remove.StandardError);
        }

        foreach (var value in values)
        {
            var add = await RunGitAsync(
                gitPath,
                ["config", "--global", "--add", key, value],
                cancellationToken).ConfigureAwait(false);
            if (!add.Succeeded)
            {
                return OperationResult.Fail($"Unable to restore global Git setting '{key}'.", add.StandardError);
            }
        }

        return OperationResult.Ok();
    }

    private Task<ProcessResult> RunGitAsync(
        string gitPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = gitPath,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(15),
            EnvironmentVariables = _environmentVariables
        }, cancellationToken);

    private string? GetEnvironmentValue(string name)
        => _environmentVariables.TryGetValue(name, out var value)
            ? value
            : Environment.GetEnvironmentVariable(name);

    private static GitConfigValue? ParseConfigValue(string output)
    {
        var line = output
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var tab = line.IndexOf('\t');
        if (tab > 0)
        {
            return new GitConfigValue(line[..tab].Trim(), line[(tab + 1)..].Trim());
        }

        var separator = line.IndexOf(' ');
        return separator > 0
            ? new GitConfigValue(line[..separator].Trim(), line[(separator + 1)..].Trim())
            : new GitConfigValue(null, line.Trim());
    }

    private static GitSshBackendKind Classify(string? command, string? variant)
    {
        var normalizedVariant = variant?.Trim().ToLowerInvariant();
        if (normalizedVariant == "auto")
        {
            normalizedVariant = null;
        }
        if (normalizedVariant is "tortoiseplink")
        {
            return GitSshBackendKind.TortoisePlink;
        }

        if (normalizedVariant is "plink" or "putty")
        {
            return GitSshBackendKind.PuttyPlink;
        }

        if (normalizedVariant is "ssh" or "simple")
        {
            return GitSshBackendKind.OpenSsh;
        }

        if (command?.Contains("tortoiseplink", StringComparison.OrdinalIgnoreCase) == true
            || (command?.Contains("tortoise", StringComparison.OrdinalIgnoreCase) == true
                && command.Contains("plink", StringComparison.OrdinalIgnoreCase)))
        {
            return GitSshBackendKind.TortoisePlink;
        }

        if (command?.Contains("plink", StringComparison.OrdinalIgnoreCase) == true
            || command?.Contains("putty", StringComparison.OrdinalIgnoreCase) == true)
        {
            return GitSshBackendKind.PuttyPlink;
        }

        var executable = ExtractExecutableName(command);
        if (executable.Contains("tortoiseplink", StringComparison.OrdinalIgnoreCase)
            || (executable.Contains("tortoise", StringComparison.OrdinalIgnoreCase)
                && executable.Contains("plink", StringComparison.OrdinalIgnoreCase)))
        {
            return GitSshBackendKind.TortoisePlink;
        }

        if (executable.Contains("plink", StringComparison.OrdinalIgnoreCase)
            || executable.Contains("putty", StringComparison.OrdinalIgnoreCase))
        {
            return GitSshBackendKind.PuttyPlink;
        }

        if (string.Equals(executable, "ssh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(executable, "ssh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return GitSshBackendKind.OpenSsh;
        }

        return string.IsNullOrWhiteSpace(command)
            && (string.IsNullOrWhiteSpace(variant)
                || string.Equals(variant.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            ? GitSshBackendKind.OpenSsh
            : GitSshBackendKind.Unknown;
    }

    private static string ExtractExecutableName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var value = command.Trim();
        string executable;
        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            executable = closingQuote > 1 ? value[1..closingQuote] : value.Trim('"');
        }
        else
        {
            var executableExtension = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableExtension >= 0)
            {
                executable = value[..(executableExtension + 4)].Trim('\'');
            }
            else
            {
                var separator = value.IndexOfAny([' ', '\t']);
                executable = separator < 0 ? value : value[..separator];
            }
        }

        return Path.GetFileName(executable.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string QuoteGitCommandPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains(' ', StringComparison.Ordinal)
            ? $"\"{normalized.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : normalized;
    }

    private static string DisplayName(GitSshBackendKind kind)
        => kind switch
        {
            GitSshBackendKind.OpenSsh => "OpenSSH",
            GitSshBackendKind.PuttyPlink => "PuTTY/Plink",
            GitSshBackendKind.TortoisePlink => "TortoisePlink",
            _ => "Unknown SSH backend"
        };

    private static bool IsCompatibleOpenSshCandidate(ExecutableInfo tool)
    {
        if (!tool.Exists || string.IsNullOrWhiteSpace(tool.SelectedPath)
            || !string.Equals(Path.GetFileName(tool.SelectedPath), "ssh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var probeText = string.Join('\n',
            tool.Version,
            tool.ProbeResult?.StandardOutput,
            tool.ProbeResult?.StandardError);
        return !probeText.Contains("PuTTY", StringComparison.OrdinalIgnoreCase)
            && !probeText.Contains("Plink", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool Equivalent(GitSshBackendInspection left, GitSshBackendInspection right)
        => left.Kind == right.Kind
            && string.Equals(left.EffectiveCommand, right.EffectiveCommand, StringComparison.Ordinal)
            && string.Equals(left.EffectiveVariant, right.EffectiveVariant, StringComparison.OrdinalIgnoreCase)
            && left.EnvironmentBlockers.SequenceEqual(right.EnvironmentBlockers, StringComparer.Ordinal);

    private sealed record GitConfigValue(string? Origin, string Value);
}
