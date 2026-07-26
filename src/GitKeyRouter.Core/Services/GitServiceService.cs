using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed class GitServiceService
{
    private readonly IAppConfigStore _configStore;
    private readonly IBackupService _backupService;
    private readonly IProcessRunner _processRunner;
    private readonly IToolchainService _toolchainService;
    private readonly GitProviderAdapterRegistry _providers;

    public GitServiceService(
        IAppConfigStore configStore,
        IBackupService backupService,
        IProcessRunner processRunner,
        IToolchainService toolchainService,
        GitProviderAdapterRegistry providers)
    {
        _configStore = configStore;
        _backupService = backupService;
        _processRunner = processRunner;
        _toolchainService = toolchainService;
        _providers = providers;
    }

    public async Task<IReadOnlyList<GitServiceInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        return config.GitServices.OrderBy(item => item.IsBuiltIn ? 0 : 1)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult<GitServiceInstance>> SaveAsync(
        GitServiceInstance service,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var existing = config.FindService(service.Id);
        if (existing?.IsBuiltIn == true)
        {
            service.Id = existing.Id;
            service.ProviderKind = existing.ProviderKind;
            service.HostName = existing.HostName;
            service.SshUser = existing.SshUser;
            service.SshPort = existing.SshPort;
            service.WebBaseUrl = existing.WebBaseUrl;
            service.AllowInsecureHttp = existing.AllowInsecureHttp;
            service.EnableExtendedSshUrlRewrites = existing.EnableExtendedSshUrlRewrites;
            service.IsBuiltIn = true;
        }

        service.Id = NormalizeId(service.Id, service.DisplayName, service.HostName);
        service.HostName = service.HostName.Trim();
        service.SshUser = service.SshUser.Trim();
        service.WebBaseUrl = service.WebBaseUrl.TrimEnd('/');
        service.SshPort ??= 22;
        var validation = GitServiceValidator.Validate(service, config);
        if (!validation.IsValid)
        {
            return OperationResult<GitServiceInstance>.Fail("Git service validation failed.", validation.Errors.ToArray());
        }

        await _backupService.CreateSnapshotAsync($"Save Git service: {service.DisplayName}", cancellationToken).ConfigureAwait(false);
        var index = config.GitServices.FindIndex(item => string.Equals(item.Id, service.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            config.GitServices[index] = service;
        }
        else
        {
            config.GitServices.Add(service);
        }

        config.SynchronizeDefaultServiceRoutes();

        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult<GitServiceInstance>.Ok(service, "Git service saved.");
    }

    public async Task<OperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var service = config.FindService(id);
        if (service is null)
        {
            return OperationResult.Fail("Git service does not exist.");
        }

        if (service.IsBuiltIn)
        {
            return OperationResult.Fail("The built-in GitHub.com service cannot be deleted.");
        }

        if (config.Identities.Any(item => string.Equals(item.ServiceInstanceId, id, StringComparison.OrdinalIgnoreCase))
            || config.RepositoryRoutes.Any(item => string.Equals(item.ServiceInstanceId, id, StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Fail("The Git service is still referenced by identities or repository routes.");
        }

        await _backupService.CreateSnapshotAsync($"Delete Git service: {service.DisplayName}", cancellationToken).ConfigureAwait(false);
        config.GitServices.Remove(service);
        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Git service deleted.");
    }

    public async Task<OperationResult<GitServiceConnectionResult>> TestConnectionAsync(
        GitServiceInstance service,
        CancellationToken cancellationToken = default)
    {
        var validation = GitServiceValidator.Validate(service, []);
        if (!validation.IsValid)
        {
            return OperationResult<GitServiceConnectionResult>.Fail("Git service validation failed.", validation.Errors.ToArray());
        }

        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.Ssh.Exists || string.IsNullOrWhiteSpace(tools.Ssh.SelectedPath))
        {
            return OperationResult<GitServiceConnectionResult>.Fail("ssh.exe was not found.");
        }

        var arguments = new List<string> { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10" };
        if (service.SshPort is > 0 and not 22)
        {
            arguments.AddRange(["-p", service.SshPort.Value.ToString()]);
        }

        arguments.Add($"{service.SshUser}@{service.HostName}");
        var process = await _processRunner.RunAsync(new ProcessRequest
        {
            ExecutablePath = tools.Ssh.SelectedPath,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(20)
        }, cancellationToken).ConfigureAwait(false);
        var adapter = _providers.Get(service.ProviderKind);
        var authenticated = adapter.IsAuthenticationSuccess(process);
        var output = process.StandardOutput + "\n" + process.StandardError;
        var classification = authenticated
            ? $"{service.ProviderKind} authentication succeeded"
            : process.TimedOut
                ? "Connection timed out"
                : output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                    ? "SSH endpoint reached; key authentication was rejected"
                    : output.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase)
                        ? "DNS resolution failed"
                        : output.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
                            ? "Host key verification failed"
                            : "SSH connection did not confirm authentication";

        var result = new GitServiceConnectionResult
        {
            Service = service,
            Process = process,
            Authenticated = authenticated,
            Classification = classification
        };
        return OperationResult<GitServiceConnectionResult>.Ok(result, classification);
    }

    public static IReadOnlyList<string> AvailableTemplates { get; } =
    [
        "Gitea Local",
        "Gitea Cloud",
        "GitLab.com",
        "自建 GitLab",
        "自建 Gitea",
        "Gogs",
        "OneDev",
        "GitBucket",
        "通用 Git 服务"
    ];

    public static GitServiceInstance CreateTemplate(string template)
        => template switch
        {
            "GitLab.com" => new GitServiceInstance
            {
                DisplayName = "GitLab.com",
                ProviderKind = GitProviderKind.GitLab,
                HostName = "gitlab.com",
                SshUser = "git",
                WebBaseUrl = "https://gitlab.com"
            },
            "自建 GitLab" => new GitServiceInstance { DisplayName = "自建 GitLab", ProviderKind = GitProviderKind.GitLab, SshUser = "git" },
            "Gitea Local" => new GitServiceInstance
            {
                Id = "gitea-local",
                DisplayName = "Gitea Local",
                ProviderKind = GitProviderKind.Gitea,
                HostName = "gitea.lan.policoil.top",
                SshPort = 22,
                SshUser = "git",
                WebBaseUrl = "https://gitea.lan.policoil.top"
            },
            "Gitea Cloud" => new GitServiceInstance
            {
                Id = "gitea-cloud",
                DisplayName = "Gitea Cloud",
                ProviderKind = GitProviderKind.Gitea,
                HostName = "git.policoil.top",
                SshPort = 22,
                SshUser = "git",
                WebBaseUrl = "https://git.policoil.top"
            },
            "自建 Gitea" => new GitServiceInstance { DisplayName = "自建 Gitea", ProviderKind = GitProviderKind.Gitea, SshPort = 22, SshUser = "git" },
            "Gogs" => CreateSelfHostedGenericTemplate("Gogs"),
            "OneDev" => CreateSelfHostedGenericTemplate("OneDev"),
            "GitBucket" => CreateSelfHostedGenericTemplate("GitBucket"),
            _ => new GitServiceInstance { DisplayName = "自定义 Git 服务", ProviderKind = GitProviderKind.Generic, SshUser = "git" }
        };

    private static GitServiceInstance CreateSelfHostedGenericTemplate(string displayName)
        => new()
        {
            DisplayName = displayName,
            ProviderKind = GitProviderKind.Generic,
            SshPort = 22,
            SshUser = "git"
        };

    private static string NormalizeId(string id, string displayName, string hostName)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim().ToLowerInvariant();
        }

        var source = string.IsNullOrWhiteSpace(hostName) ? displayName : hostName;
        var normalized = new string(source.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N") : normalized;
    }

}
