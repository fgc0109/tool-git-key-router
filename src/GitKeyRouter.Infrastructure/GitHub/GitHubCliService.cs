using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Infrastructure.GitHub;

public sealed record GitHubCliCommandResult(
    bool Success,
    int ExitCode,
    string Message,
    string? IdentityId = null,
    string? AccountName = null,
    string? ConfigDirectory = null);

public sealed record GitHubCliResolutionResult(
    bool Success,
    int ExitCode,
    string Message,
    string? GitHubCliPath = null,
    string? GitHubCliSource = null,
    string? GitHubCliVersion = null,
    IReadOnlyList<string>? GitHubCliCandidates = null,
    string? RepositoryRoot = null,
    string? RemoteName = null,
    string? RemoteSelectionSource = null,
    IReadOnlyList<string>? PushUrls = null,
    string? HostAlias = null,
    string? IdentityId = null,
    string? AccountName = null,
    string? GitHubHost = null,
    string? GitHubRepository = null,
    IReadOnlyList<string>? Warnings = null);

public sealed class GitHubCliService
{
    private static readonly Version MinimumSupportedGitHubCliVersion = new(2, 40, 0);

    private static readonly string[] TokenEnvironmentVariables =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GH_ENTERPRISE_TOKEN",
        "GITHUB_ENTERPRISE_TOKEN"
    ];

    private static readonly Regex PlaintextTokenPattern = new(
        @"^\s*oauth_token\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IAppConfigStore _configStore;
    private readonly IAppPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly IToolchainService _toolchainService;

    public GitHubCliService(
        IAppConfigStore configStore,
        IAppPaths paths,
        IProcessRunner processRunner,
        IToolchainService toolchainService)
    {
        _configStore = configStore;
        _paths = paths;
        _processRunner = processRunner;
        _toolchainService = toolchainService;
    }

    public async Task<GitHubCliCommandResult> LoginAsync(
        string identitySelector,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveExplicitIdentityAsync(identitySelector, cancellationToken).ConfigureAwait(false);
        if (!context.Success || context.Identity is null || context.Service is null)
        {
            return context.ToCommandResult();
        }

        var executable = await GetGitHubCliExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (!executable.Success || executable.Path is null)
        {
            return Failure(executable.Error, 2, context);
        }

        var configDirectory = GetConfigDirectory(context.Identity);
        Directory.CreateDirectory(configDirectory);
        await using var identityLock = await AcquireIdentityLockAsync(
            configDirectory,
            cancellationToken).ConfigureAwait(false);
        if (identityLock is null)
        {
            return Failure(
                $"Another GitHub CLI account operation is already running for identity '{context.Identity.HostAlias}'.",
                2,
                context);
        }

        var environment = BuildEnvironment(configDirectory, context.Service.HostName);
        var login = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = executable.Path,
            Arguments =
            [
                "auth",
                "login",
                "--hostname",
                context.Service.HostName,
                "--git-protocol",
                "ssh",
                "--web",
                "--skip-ssh-key"
            ],
            EnvironmentVariables = environment,
            IoMode = ProcessIoMode.InheritConsole,
            CreateNoWindow = false,
            IncludeArgumentsInResult = false,
            Timeout = TimeSpan.FromMinutes(30),
            TerminationWaitTimeout = TimeSpan.FromSeconds(5)
        }, cancellationToken).ConfigureAwait(false);

        if (!login.Succeeded)
        {
            return Failure(
                login.StartException is null
                    ? $"GitHub CLI login failed with exit code {login.ExitCode?.ToString() ?? "<none>"}."
                    : $"GitHub CLI login could not start: {login.StartException.Message}",
                login.ExitCode ?? 2,
                context);
        }

        var plaintextWarning = DetectPlaintextToken(configDirectory);
        if (plaintextWarning is not null)
        {
            return Failure(plaintextWarning, 2, context);
        }

        var verification = await VerifyAccountAsync(
            executable.Path,
            context,
            configDirectory,
            cancellationToken).ConfigureAwait(false);
        return verification;
    }

    public async Task<GitHubCliCommandResult> StatusAsync(
        string? identitySelector,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = string.IsNullOrWhiteSpace(identitySelector)
            ? await ResolveRepositoryIdentityAsync(
                workingDirectory ?? Environment.CurrentDirectory,
                cancellationToken).ConfigureAwait(false)
            : await ResolveExplicitIdentityAsync(identitySelector, cancellationToken).ConfigureAwait(false);
        if (!context.Success || context.Identity is null || context.Service is null)
        {
            return context.ToCommandResult();
        }

        var executable = await GetGitHubCliExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (!executable.Success || executable.Path is null)
        {
            return Failure(executable.Error, 2, context);
        }

        var configDirectory = GetConfigDirectory(context.Identity);
        var plaintextWarning = DetectPlaintextToken(configDirectory);
        if (plaintextWarning is not null)
        {
            return Failure(plaintextWarning, 2, context);
        }

        return await VerifyAccountAsync(
            executable.Path,
            context,
            configDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubCliCommandResult> RunAsync(
        IReadOnlyList<string> ghArguments,
        string? explicitIdentity,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (ghArguments.Count == 0)
        {
            return new GitHubCliCommandResult(false, 3, "No GitHub CLI arguments were supplied.");
        }

        var unsafeCommand = FindUnsafeWrappedCommand(ghArguments);
        if (unsafeCommand is not null)
        {
            return new GitHubCliCommandResult(false, 3, unsafeCommand);
        }

        var repositorySelector = GetRepositoryOption(ghArguments);
        IdentityContext context;
        if (!string.IsNullOrWhiteSpace(explicitIdentity))
        {
            context = await ResolveExplicitIdentityAsync(explicitIdentity, cancellationToken).ConfigureAwait(false);
            if (context.Success && repositorySelector is not null)
            {
                context = await ValidateRepositorySelectorAsync(
                    context,
                    repositorySelector,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (repositorySelector is not null)
        {
            context = await ResolveRepositorySelectorAsync(
                repositorySelector,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context = await ResolveRepositoryIdentityAsync(
                workingDirectory ?? Environment.CurrentDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        if (!context.Success || context.Identity is null || context.Service is null)
        {
            return context.ToCommandResult();
        }

        var executable = await GetGitHubCliExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (!executable.Success || executable.Path is null)
        {
            return Failure(executable.Error, 2, context);
        }

        var configDirectory = GetConfigDirectory(context.Identity);
        var plaintextWarning = DetectPlaintextToken(configDirectory);
        if (plaintextWarning is not null)
        {
            return Failure(plaintextWarning, 2, context);
        }

        var verification = await VerifyAccountAsync(
            executable.Path,
            context,
            configDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!verification.Success)
        {
            return verification;
        }

        var process = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = executable.Path,
            Arguments = ghArguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            EnvironmentVariables = BuildEnvironment(
                configDirectory,
                context.Service.HostName,
                context.RepositorySelector),
            IoMode = ProcessIoMode.InheritConsole,
            CreateNoWindow = false,
            IncludeArgumentsInResult = false,
            Timeout = TimeSpan.FromHours(24),
            TerminationWaitTimeout = TimeSpan.FromSeconds(5)
        }, cancellationToken).ConfigureAwait(false);

        if (process.StartException is not null)
        {
            return Failure(
                $"GitHub CLI could not start: {process.StartException.Message}",
                2,
                context);
        }

        var exitCode = process.ExitCode ?? (process.Cancelled ? 130 : 2);
        return new GitHubCliCommandResult(
            exitCode == 0,
            exitCode,
            exitCode == 0
                ? $"GitHub CLI completed as '{context.Identity.AccountName}' through identity '{context.Identity.HostAlias}'."
                : $"GitHub CLI exited with code {exitCode} through identity '{context.Identity.HostAlias}'.",
            context.Identity.Id,
            context.Identity.AccountName,
            configDirectory);
    }

    public async Task<GitHubCliResolutionResult> ResolveAsync(
        string? explicitIdentity,
        string? repositorySelector,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        IdentityContext context;
        if (!string.IsNullOrWhiteSpace(explicitIdentity))
        {
            context = await ResolveExplicitIdentityAsync(explicitIdentity, cancellationToken).ConfigureAwait(false);
            if (context.Success && !string.IsNullOrWhiteSpace(repositorySelector))
            {
                context = await ValidateRepositorySelectorAsync(
                    context,
                    repositorySelector,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (!string.IsNullOrWhiteSpace(repositorySelector))
        {
            context = await ResolveRepositorySelectorAsync(
                repositorySelector,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context = await ResolveRepositoryIdentityAsync(
                workingDirectory ?? Environment.CurrentDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        var executable = await GetGitHubCliExecutableAsync(cancellationToken).ConfigureAwait(false);
        var success = context.Success && executable.Success;
        var message = success
            ? $"GitHub CLI resolves to identity '{context.Identity!.HostAlias}' ({context.Identity.AccountName})."
            : string.Join(
                Environment.NewLine,
                new[]
                {
                    context.Success ? null : context.Error,
                    executable.Success ? null : executable.Error
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new GitHubCliResolutionResult(
            success,
            success ? 0 : 3,
            message,
            executable.Path,
            executable.Source,
            executable.Version,
            executable.CandidatePaths,
            context.RepositoryRoot,
            context.RemoteName,
            context.RemoteSelectionSource,
            context.PushUrls,
            context.HostAlias,
            context.Identity?.Id,
            context.Identity?.AccountName,
            context.Service?.HostName,
            context.RepositorySelector,
            context.Warnings);
    }

    public string GetConfigDirectory(GitIdentity identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.Id));
        var directoryName = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return Path.Combine(_paths.AppDataDirectory, "github-cli", directoryName);
    }

    private async Task<IdentityContext> ResolveExplicitIdentityAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        var config = await LoadNormalizedConfigAsync(cancellationToken).ConfigureAwait(false);
        var matches = config.Identities
            .Where(identity => IsGitHubIdentity(config, identity)
                && (string.Equals(identity.Id, selector, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(identity.HostAlias, selector, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0)
        {
            return IdentityContext.Fail(
                $"No GitHub identity matches '{selector}'. Use an identity ID or SSH HostAlias.");
        }

        if (matches.Count > 1)
        {
            return IdentityContext.Fail($"Identity selector '{selector}' is ambiguous.");
        }

        return CreateContext(config, matches[0]);
    }

    private async Task<IdentityContext> ResolveRepositorySelectorAsync(
        string repositorySelector,
        CancellationToken cancellationToken)
    {
        var config = await LoadNormalizedConfigAsync(cancellationToken).ConfigureAwait(false);
        if (!TryParseRepositorySelector(repositorySelector, config, out var service, out var owner, out var repository, out var error))
        {
            return IdentityContext.Fail(error);
        }

        return ResolveRoute(config, service!, owner!, repository!);
    }

    private async Task<IdentityContext> ValidateRepositorySelectorAsync(
        IdentityContext explicitContext,
        string repositorySelector,
        CancellationToken cancellationToken)
    {
        if (!explicitContext.Success || explicitContext.Identity is null)
        {
            return explicitContext;
        }

        var routed = await ResolveRepositorySelectorAsync(repositorySelector, cancellationToken).ConfigureAwait(false);
        if (!routed.Success || routed.Identity is null)
        {
            return routed;
        }

        return string.Equals(
                routed.Identity.Id,
                explicitContext.Identity.Id,
                StringComparison.OrdinalIgnoreCase)
            ? explicitContext
            : IdentityContext.Fail(
                $"Explicit identity '{explicitContext.Identity.HostAlias}' conflicts with the configured route for '{repositorySelector}', which selects '{routed.Identity.HostAlias}'.");
    }

    private async Task<IdentityContext> ResolveRepositoryIdentityAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var config = await LoadNormalizedConfigAsync(cancellationToken).ConfigureAwait(false);
        var toolchain = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!toolchain.Git.Exists || string.IsNullOrWhiteSpace(toolchain.Git.SelectedPath))
        {
            return IdentityContext.Fail("Git was not found; specify --identity explicitly.");
        }

        var git = toolchain.Git.SelectedPath;
        var repositoryRoot = await RunGitTextAsync(
            git,
            workingDirectory,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (!repositoryRoot.Success)
        {
            return IdentityContext.Fail(
                "The current directory is not a Git repository; specify --identity explicitly.");
        }

        var remoteNamesResult = await RunGitTextAsync(
            git,
            repositoryRoot.Text,
            ["remote"],
            cancellationToken).ConfigureAwait(false);
        if (!remoteNamesResult.Success)
        {
            return IdentityContext.Fail("Git remotes could not be enumerated.");
        }

        var remoteNames = SplitLines(remoteNamesResult.Text);
        if (remoteNames.Count == 0)
        {
            return IdentityContext.Fail(
                "The repository has no Git remotes; specify --identity explicitly.");
        }

        var remoteUrls = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var remoteName in remoteNames)
        {
            var urls = await GetRemoteUrlsAsync(
                git,
                repositoryRoot.Text,
                remoteName,
                cancellationToken).ConfigureAwait(false);
            if (urls.Count > 0)
            {
                remoteUrls[remoteName] = urls;
            }
        }

        var selectedRemote = await SelectRemoteAsync(
            git,
            repositoryRoot.Text,
            remoteNames,
            cancellationToken).ConfigureAwait(false);
        if (selectedRemote is null)
        {
            return IdentityContext.Fail(
                "No branch pushRemote, remote.pushDefault, tracking remote, or origin remote could be selected. Use --identity explicitly.");
        }

        if (!remoteUrls.TryGetValue(selectedRemote.Name, out var selectedUrls))
        {
            selectedUrls = await GetRemoteUrlsAsync(
                git,
                repositoryRoot.Text,
                selectedRemote.Name,
                cancellationToken).ConfigureAwait(false);
        }

        if (selectedUrls.Count == 0)
        {
            return IdentityContext.Fail(
                $"Remote '{selectedRemote.Name}' has no usable push or fetch URL.");
        }

        IdentityContext? selectedContext = null;
        string? selectedHostAlias = null;
        foreach (var selectedUrl in selectedUrls)
        {
            if (!TryParseRepositoryRemote(
                    selectedUrl,
                    out var remoteHost,
                    out var owner,
                    out var repository,
                    out var usesSsh))
            {
                return IdentityContext.Fail(
                    $"Remote '{selectedRemote.Name}' contains an unsupported repository URL.");
            }

            var aliasedIdentity = usesSsh
                ? FindIdentityByHostAlias(config, remoteHost)
                : null;
            var service = aliasedIdentity is null
                ? FindGitHubServiceByHost(config, remoteHost)
                : config.FindService(aliasedIdentity.ServiceInstanceId);
            if (service is null)
            {
                return IdentityContext.Fail(
                    usesSsh
                        ? $"SSH HostAlias '{remoteHost}' is not a configured GitHub identity or service host."
                        : $"HTTPS host '{remoteHost}' is not a configured GitHub service.");
            }

            var routed = ResolveRoute(config, service, owner, repository);
            if (!routed.Success || routed.Identity is null)
            {
                return routed;
            }

            if (aliasedIdentity is not null
                && !string.Equals(
                    aliasedIdentity.Id,
                    routed.Identity.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return IdentityContext.Fail(
                    $"Remote '{selectedRemote.Name}' uses SSH HostAlias '{remoteHost}', but the configured route for '{owner}/{repository}' selects '{routed.Identity.HostAlias}'.");
            }

            if (selectedContext is not null
                && (!string.Equals(
                        selectedContext.Identity!.Id,
                        routed.Identity.Id,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        selectedContext.RepositorySelector,
                        routed.RepositorySelector,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return IdentityContext.Fail(
                    $"Remote '{selectedRemote.Name}' has push URLs that resolve to different GitHub identities or repositories.");
            }

            selectedContext = routed;
            selectedHostAlias ??= aliasedIdentity?.HostAlias;
        }

        var warnings = new List<string>();
        foreach (var pair in remoteUrls.Where(pair => !string.Equals(
                     pair.Key,
                     selectedRemote.Name,
                     StringComparison.OrdinalIgnoreCase)))
        {
            var otherIdentity = pair.Value
                .Select(url => TryParseRepositoryRemote(url, out var host, out _, out _, out var usesSsh)
                    && usesSsh
                        ? FindIdentityByHostAlias(config, host)
                        : null)
                .FirstOrDefault(identity => identity is not null);
            if (otherIdentity is not null
                && !string.Equals(
                    otherIdentity.Id,
                    selectedContext!.Identity!.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(
                    $"Remote '{pair.Key}' routes to identity '{otherIdentity.HostAlias}', but it is not the selected push remote.");
            }
        }

        return selectedContext! with
        {
            RepositoryRoot = repositoryRoot.Text,
            RemoteName = selectedRemote.Name,
            RemoteSelectionSource = selectedRemote.Source,
            PushUrls = selectedUrls,
            HostAlias = selectedHostAlias ?? selectedContext!.Identity!.HostAlias,
            Warnings = warnings
        };
    }

    private IdentityContext ResolveRoute(
        AppConfig config,
        GitServiceInstance service,
        string owner,
        string repository)
    {
        var repositoryWithSuffix = repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repository
            : repository + ".git";
        var repositoryPath = $"{owner}/{repositoryWithSuffix}";

        var candidates = config.RepositoryRoutes
            .Where(route => route.Enabled
                && string.Equals(
                    route.ServiceInstanceId,
                    service.Id,
                    StringComparison.OrdinalIgnoreCase)
                && RouteMatches(route, owner, repositoryPath))
            .Select(route => new
            {
                Route = route,
                Specificity = route.Scope switch
                {
                    GitRouteScope.Repository => 3,
                    GitRouteScope.Owner => 2,
                    GitRouteScope.Service => 1,
                    _ => 0
                }
            })
            .OrderByDescending(item => item.Specificity)
            .ToList();

        if (candidates.Count == 0)
        {
            return IdentityContext.Fail(
                $"No enabled GitKeyRouter identity route matches '{owner}/{repository}' on '{service.HostName}'.");
        }

        var highestSpecificity = candidates[0].Specificity;
        var identityIds = candidates
            .Where(item => item.Specificity == highestSpecificity)
            .Select(item => item.Route.IdentityId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (identityIds.Count != 1)
        {
            return IdentityContext.Fail(
                $"The configured identity route for '{owner}/{repository}' is ambiguous.");
        }

        var identities = config.Identities.Where(item =>
            string.Equals(item.Id, identityIds[0], StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (identities.Count != 1 || !IsGitHubIdentity(config, identities[0]))
        {
            return IdentityContext.Fail(
                $"The configured route for '{owner}/{repository}' does not reference one unique GitHub identity.");
        }

        var context = CreateContext(config, identities[0]);
        return context.Success
            ? context with
            {
                RepositorySelector = $"{service.HostName}/{owner}/{StripGitSuffix(repository)}"
            }
            : context;
    }

    private async Task<GitHubCliCommandResult> VerifyAccountAsync(
        string executable,
        IdentityContext context,
        string configDirectory,
        CancellationToken cancellationToken)
    {
        var process = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = executable,
            Arguments = ["api", "user", "--jq", ".login"],
            EnvironmentVariables = BuildEnvironment(configDirectory, context.Service!.HostName),
            IncludeArgumentsInResult = false,
            Timeout = TimeSpan.FromSeconds(30)
        }, cancellationToken).ConfigureAwait(false);

        if (!process.Succeeded)
        {
            return Failure(
                process.StartException is null
                    ? "GitHub CLI did not return the authenticated account for this identity."
                    : $"GitHub CLI account verification could not start: {process.StartException.Message}",
                process.ExitCode ?? 2,
                context);
        }

        var actualAccount = process.StandardOutput.Trim();
        if (actualAccount.Length == 0)
        {
            return Failure("GitHub CLI returned an empty authenticated account.", 2, context);
        }

        if (!string.Equals(
                actualAccount,
                context.Identity!.AccountName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"GitHub CLI authenticated as '{actualAccount}', but identity '{context.Identity.HostAlias}' expects '{context.Identity.AccountName}'.",
                2,
                context);
        }

        return new GitHubCliCommandResult(
            true,
            0,
            $"GitHub CLI identity '{context.Identity.HostAlias}' is authenticated as '{actualAccount}'.",
            context.Identity.Id,
            actualAccount,
            configDirectory);
    }

    private async Task<GitHubCliExecutableResult> GetGitHubCliExecutableAsync(
        CancellationToken cancellationToken)
    {
        var toolchain = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!toolchain.Gh.Exists || string.IsNullOrWhiteSpace(toolchain.Gh.SelectedPath))
        {
            return GitHubCliExecutableResult.Fail(
                "GitHub CLI (gh.exe) was not found.",
                candidatePaths: toolchain.Gh.CandidatePaths);
        }

        if (!TryParseGitHubCliVersion(toolchain.Gh.Version, out var version))
        {
            return GitHubCliExecutableResult.Fail(
                $"GitHub CLI version could not be determined. Version {MinimumSupportedGitHubCliVersion} or later is required for isolated multi-account routing.",
                toolchain.Gh.SelectedPath,
                toolchain.Gh.SelectedSource,
                toolchain.Gh.Version,
                toolchain.Gh.CandidatePaths);
        }

        if (version < MinimumSupportedGitHubCliVersion)
        {
            return GitHubCliExecutableResult.Fail(
                $"GitHub CLI {version} is not supported. Version {MinimumSupportedGitHubCliVersion} or later is required for isolated multi-account routing.",
                toolchain.Gh.SelectedPath,
                toolchain.Gh.SelectedSource,
                toolchain.Gh.Version,
                toolchain.Gh.CandidatePaths);
        }

        return new GitHubCliExecutableResult(
            true,
            toolchain.Gh.SelectedPath,
            toolchain.Gh.SelectedSource,
            toolchain.Gh.Version,
            toolchain.Gh.CandidatePaths,
            string.Empty);
    }

    private static bool TryParseGitHubCliVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Regex.Match(
            value,
            @"(?<!\d)(?<version>\d+\.\d+\.\d+)(?!\d)",
            RegexOptions.CultureInvariant);
        return match.Success
            && Version.TryParse(match.Groups["version"].Value, out version!);
    }

    private async Task<AppConfig> LoadNormalizedConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        config.Normalize();
        return config;
    }

    private IdentityContext CreateContext(AppConfig config, GitIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Id)
            || config.Identities.Count(item => string.Equals(
                item.Id,
                identity.Id,
                StringComparison.OrdinalIgnoreCase)) != 1)
        {
            return IdentityContext.Fail(
                $"Identity '{identity.HostAlias}' must have one unique, non-empty stable ID before GitHub CLI routing can be used.");
        }

        if (string.IsNullOrWhiteSpace(identity.HostAlias)
            || config.Identities.Count(item => string.Equals(
                item.HostAlias,
                identity.HostAlias,
                StringComparison.OrdinalIgnoreCase)) != 1)
        {
            return IdentityContext.Fail(
                "GitHub CLI routing requires one unique, non-empty SSH HostAlias for the selected identity.");
        }

        if (string.IsNullOrWhiteSpace(identity.AccountName))
        {
            return IdentityContext.Fail(
                $"Identity '{identity.HostAlias}' must define the expected GitHub AccountName.");
        }

        var service = config.FindService(identity.ServiceInstanceId);
        if (service is null || service.ProviderKind != GitProviderKind.GitHub)
        {
            return IdentityContext.Fail(
                $"Identity '{identity.HostAlias}' is not associated with a GitHub service.");
        }

        return new IdentityContext(true, string.Empty, config, service, identity);
    }

    private static bool IsGitHubIdentity(AppConfig config, GitIdentity identity)
        => config.FindService(identity.ServiceInstanceId)?.ProviderKind == GitProviderKind.GitHub;

    private static GitIdentity? FindIdentityByHostAlias(AppConfig config, string hostAlias)
    {
        var matches = config.Identities
            .Where(identity => IsGitHubIdentity(config, identity)
                && string.Equals(identity.HostAlias, hostAlias, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static GitServiceInstance? FindGitHubServiceByHost(AppConfig config, string hostName)
    {
        var matches = config.GitServices
            .Where(service => service.ProviderKind == GitProviderKind.GitHub
                && string.Equals(service.HostName, hostName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool RouteMatches(RepositoryRoute route, string owner, string repositoryPath)
        => route.Scope switch
        {
            GitRouteScope.Service => true,
            GitRouteScope.Owner => string.Equals(
                route.Owner ?? route.NamespacePath,
                owner,
                StringComparison.OrdinalIgnoreCase),
            GitRouteScope.Repository => string.Equals(
                route.RoutePath,
                repositoryPath,
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private bool TryParseRepositorySelector(
        string selector,
        AppConfig config,
        out GitServiceInstance? service,
        out string? owner,
        out string? repository,
        out string error)
    {
        service = null;
        owner = null;
        repository = null;
        error = string.Empty;

        var trimmed = selector.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            error = "The --repo value is empty.";
            return false;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 2)
        {
            service = config.FindService(GitServiceInstance.GitHubComId)
                ?? config.GitServices.FirstOrDefault(item => item.ProviderKind == GitProviderKind.GitHub);
            owner = segments[0];
            repository = StripGitSuffix(segments[1]);
        }
        else if (segments.Length == 3)
        {
            service = config.GitServices.FirstOrDefault(item =>
                item.ProviderKind == GitProviderKind.GitHub
                && string.Equals(item.HostName, segments[0], StringComparison.OrdinalIgnoreCase));
            owner = segments[1];
            repository = StripGitSuffix(segments[2]);
        }
        else
        {
            error = $"Repository selector '{selector}' must be OWNER/REPO or HOST/OWNER/REPO.";
            return false;
        }

        if (service is null)
        {
            error = $"No configured GitHub service matches repository selector '{selector}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            error = $"Repository selector '{selector}' is incomplete.";
            return false;
        }

        return true;
    }

    private static bool TryParseSshRemote(
        string remoteUrl,
        out string host,
        out string owner,
        out string repository)
    {
        host = string.Empty;
        owner = string.Empty;
        repository = string.Empty;
        var value = remoteUrl.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            host = uri.Host;
            return TryParseRepositoryPath(uri.AbsolutePath, out owner, out repository);
        }

        if (value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/'))
        {
            return false;
        }

        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var hostPart = value[..colon];
        var at = hostPart.LastIndexOf('@');
        host = at >= 0 ? hostPart[(at + 1)..] : hostPart;
        return host.Length > 0
            && TryParseRepositoryPath(value[(colon + 1)..], out owner, out repository);
    }

    private static bool TryParseRepositoryRemote(
        string remoteUrl,
        out string host,
        out string owner,
        out string repository,
        out bool usesSsh)
    {
        if (TryParseSshRemote(remoteUrl, out host, out owner, out repository))
        {
            usesSsh = true;
            return true;
        }

        host = string.Empty;
        owner = string.Empty;
        repository = string.Empty;
        usesSsh = false;
        return Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && (host = uri.Host).Length > 0
            && TryParseRepositoryPath(uri.AbsolutePath, out owner, out repository);
    }

    private static bool TryParseRepositoryPath(
        string path,
        out string owner,
        out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        owner = string.Join('/', segments[..^1]);
        repository = StripGitSuffix(segments[^1]);
        return owner.Length > 0 && repository.Length > 0;
    }

    private async Task<RemoteSelection?> SelectRemoteAsync(
        string git,
        string repositoryRoot,
        IReadOnlyCollection<string> remoteNames,
        CancellationToken cancellationToken)
    {
        var branch = await RunGitTextAsync(
            git,
            repositoryRoot,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        if (branch.Success && branch.Text.Length > 0)
        {
            var branchPushRemote = await RunGitTextAsync(
                git,
                repositoryRoot,
                ["config", "--get", $"branch.{branch.Text}.pushRemote"],
                cancellationToken).ConfigureAwait(false);
            if (branchPushRemote.Success
                && remoteNames.Contains(branchPushRemote.Text, StringComparer.OrdinalIgnoreCase))
            {
                return new RemoteSelection(branchPushRemote.Text, "branch.pushRemote");
            }

            var pushDefault = await RunGitTextAsync(
                git,
                repositoryRoot,
                ["config", "--get", "remote.pushDefault"],
                cancellationToken).ConfigureAwait(false);
            if (pushDefault.Success
                && remoteNames.Contains(pushDefault.Text, StringComparer.OrdinalIgnoreCase))
            {
                return new RemoteSelection(pushDefault.Text, "remote.pushDefault");
            }

            var tracking = await RunGitTextAsync(
                git,
                repositoryRoot,
                ["config", "--get", $"branch.{branch.Text}.remote"],
                cancellationToken).ConfigureAwait(false);
            if (tracking.Success
                && tracking.Text != "."
                && remoteNames.Contains(tracking.Text, StringComparer.OrdinalIgnoreCase))
            {
                return new RemoteSelection(tracking.Text, "branch.remote");
            }
        }
        else
        {
            var pushDefault = await RunGitTextAsync(
                git,
                repositoryRoot,
                ["config", "--get", "remote.pushDefault"],
                cancellationToken).ConfigureAwait(false);
            if (pushDefault.Success
                && remoteNames.Contains(pushDefault.Text, StringComparer.OrdinalIgnoreCase))
            {
                return new RemoteSelection(pushDefault.Text, "remote.pushDefault");
            }
        }

        return remoteNames.Contains("origin", StringComparer.OrdinalIgnoreCase)
            ? new RemoteSelection("origin", "origin fallback")
            : null;
    }

    private async Task<IReadOnlyList<string>> GetRemoteUrlsAsync(
        string git,
        string repositoryRoot,
        string remote,
        CancellationToken cancellationToken)
    {
        var push = await RunGitTextAsync(
            git,
            repositoryRoot,
            ["remote", "get-url", "--push", "--all", remote],
            cancellationToken).ConfigureAwait(false);
        if (push.Success && push.Text.Length > 0)
        {
            return SplitLines(push.Text);
        }

        var fetch = await RunGitTextAsync(
            git,
            repositoryRoot,
            ["remote", "get-url", "--all", remote],
            cancellationToken).ConfigureAwait(false);
        return fetch.Success && fetch.Text.Length > 0 ? SplitLines(fetch.Text) : [];
    }

    private async Task<GitTextResult> RunGitTextAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            IncludeArgumentsInResult = false,
            Timeout = TimeSpan.FromSeconds(15)
        }, cancellationToken).ConfigureAwait(false);
        return new GitTextResult(
            result.Succeeded,
            result.StandardOutput.Trim());
    }

    private static IReadOnlyList<string> SplitLines(string value)
        => value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyDictionary<string, string?> BuildEnvironment(
        string configDirectory,
        string hostName,
        string? repositorySelector = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GH_CONFIG_DIR"] = configDirectory,
            ["GH_HOST"] = hostName,
            ["GH_REPO"] = repositorySelector
        };
        foreach (var name in TokenEnvironmentVariables)
        {
            environment[name] = null;
        }

        return environment;
    }

    private static string? GetRepositoryOption(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "-R", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < arguments.Count ? arguments[index + 1] : string.Empty;
            }

            if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                return argument["--repo=".Length..];
            }

            if (argument.StartsWith("-R", StringComparison.OrdinalIgnoreCase)
                && argument.Length > 2)
            {
                return argument[2..];
            }
        }

        return null;
    }

    private static string? FindUnsafeWrappedCommand(IReadOnlyList<string> arguments)
    {
        string? firstCommand = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "-R", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase)
                || (argument.StartsWith("-R", StringComparison.OrdinalIgnoreCase)
                    && argument.Length > 2)
                || argument.StartsWith('-'))
            {
                continue;
            }

            firstCommand = argument;
            break;
        }

        if (firstCommand is not null
            && (string.Equals(firstCommand, "auth", StringComparison.OrdinalIgnoreCase)
                || string.Equals(firstCommand, "config", StringComparison.OrdinalIgnoreCase)
                || string.Equals(firstCommand, "alias", StringComparison.OrdinalIgnoreCase)))
        {
            return $"Wrapped 'gh {firstCommand}' commands are blocked because they can change account selection or per-identity configuration. Use gh-login or gh-status instead.";
        }

        if (arguments.Any(argument =>
                string.Equals(argument, "--hostname", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--hostname=", StringComparison.OrdinalIgnoreCase)))
        {
            return "Wrapped --hostname is blocked because GitKeyRouter selects the GitHub host from the routed identity.";
        }

        return null;
    }

    private static string? DetectPlaintextToken(string configDirectory)
    {
        var hostsPath = Path.Combine(configDirectory, "hosts.yml");
        if (!File.Exists(hostsPath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(hostsPath))
            {
                if (PlaintextTokenPattern.IsMatch(line))
                {
                    return "GitHub CLI stored an oauth_token in hosts.yml instead of the system credential store. This identity is blocked until the plaintext credential is removed and login is repeated with a working secure credential store.";
                }
            }
        }
        catch (IOException exception)
        {
            return $"GitHub CLI credential metadata could not be inspected safely: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"GitHub CLI credential metadata could not be inspected safely: {exception.Message}";
        }

        return null;
    }

    private static async Task<FileStream?> AcquireIdentityLockAsync(
        string configDirectory,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(configDirectory, ".gitkeyrouter.lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private GitHubCliCommandResult Failure(
        string message,
        int exitCode,
        IdentityContext context)
        => new(
            false,
            exitCode,
            message,
            context.Identity?.Id,
            context.Identity?.AccountName,
            context.Identity is null ? null : GetConfigDirectory(context.Identity));

    private static string StripGitSuffix(string value)
        => value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;

    private sealed record GitTextResult(bool Success, string Text);

    private sealed record RemoteSelection(string Name, string Source);

    private sealed record GitHubCliExecutableResult(
        bool Success,
        string? Path,
        string? Source,
        string? Version,
        IReadOnlyList<string> CandidatePaths,
        string Error)
    {
        public static GitHubCliExecutableResult Fail(
            string error,
            string? path = null,
            string? source = null,
            string? version = null,
            IReadOnlyList<string>? candidatePaths = null)
            => new(false, path, source, version, candidatePaths ?? [], error);
    }

    private sealed record IdentityContext(
        bool Success,
        string Error,
        AppConfig? Config,
        GitServiceInstance? Service,
        GitIdentity? Identity,
        string? RepositorySelector = null,
        string? RepositoryRoot = null,
        string? RemoteName = null,
        string? RemoteSelectionSource = null,
        IReadOnlyList<string>? PushUrls = null,
        string? HostAlias = null,
        IReadOnlyList<string>? Warnings = null)
    {
        public static IdentityContext Fail(string error)
            => new(false, error, null, null, null);

        public GitHubCliCommandResult ToCommandResult()
            => new(
                false,
                3,
                Error,
                Identity?.Id,
                Identity?.AccountName);
    }
}
