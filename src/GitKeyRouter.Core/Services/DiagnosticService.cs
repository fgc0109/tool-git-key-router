using System.Text.RegularExpressions;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Services;

public sealed partial class DiagnosticService
{
    private readonly IAppConfigStore _configStore;
    private readonly IAppPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IToolchainService _toolchainService;
    private readonly SshConfigService _sshConfigService;
    private readonly GitUrlRewriteService _gitUrlRewriteService;
    private readonly IClock _clock;
    private readonly GitProviderAdapterRegistry _providers;

    public DiagnosticService(
        IAppConfigStore configStore,
        IAppPaths paths,
        IFileSystem fileSystem,
        IToolchainService toolchainService,
        SshConfigService sshConfigService,
        GitUrlRewriteService gitUrlRewriteService,
        IClock clock,
        GitProviderAdapterRegistry? providers = null)
    {
        _configStore = configStore;
        _paths = paths;
        _fileSystem = fileSystem;
        _toolchainService = toolchainService;
        _sshConfigService = sshConfigService;
        _gitUrlRewriteService = gitUrlRewriteService;
        _clock = clock;
        _providers = providers ?? GitProviderAdapterRegistry.CreateDefault();
    }

    public async Task<DiagnosticReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var report = new DiagnosticReport { GeneratedAt = _clock.UtcNow };
        AddEnvironmentItems(report);
        Add(report, "APPLICATION_VERSION", "Environment", "GitKeyRouter version",
            typeof(DiagnosticService).Assembly.GetName().Version?.ToString() ?? "Unknown",
            DiagnosticSeverity.Normal);

        var tools = await _toolchainService.InspectAsync(cancellationToken).ConfigureAwait(false);
        AddTool(report, tools.Git);
        AddTool(report, tools.Ssh);
        AddTool(report, tools.SshKeygen);

        AppConfig config;
        try
        {
            config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            Add(report, "APP_CONFIG", "Configuration", "Application configuration", _configStore.ConfigPath,
                DiagnosticSeverity.Normal);
        }
        catch (Exception exception)
        {
            Add(report, "APP_CONFIG_INVALID", "Configuration", "Application configuration cannot be read", exception.Message,
                DiagnosticSeverity.Error, "Correct config.json or restore a backup. GitKeyRouter will not overwrite the damaged file.");
            return report;
        }

        AddServiceAndRouteChecks(report, config);
        await AddIdentityChecksAsync(report, config, cancellationToken).ConfigureAwait(false);
        await AddSshConfigChecksAsync(report, config, cancellationToken).ConfigureAwait(false);
        await AddGitChecksAsync(report, config, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private void AddServiceAndRouteChecks(DiagnosticReport report, AppConfig config)
    {
        foreach (var route in config.RepositoryRoutes)
        {
            route.Normalize();
        }

        foreach (var duplicate in config.GitServices
                     .GroupBy(item => item.WebBaseUrl.Trim().TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
        {
            Add(report, "SERVICE_WEB_SOURCE_DUPLICATE", "Git services", "WebBaseUrl is declared by multiple services",
                $"Source: {duplicate.Key}{Environment.NewLine}Services: {string.Join(", ", duplicate.Select(item => item.DisplayName))}",
                DiagnosticSeverity.Error, "Give every independent Git service a unique WebBaseUrl.");
        }

        foreach (var duplicate in config.GitServices
                     .GroupBy(item => item.HostName.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
        {
            Add(report, "SERVICE_SSH_SOURCE_DUPLICATE", "Git services", "SSH host source is declared by multiple services",
                $"Source: {duplicate.Key}{Environment.NewLine}Services: {string.Join(", ", duplicate.Select(item => item.DisplayName))}",
                DiagnosticSeverity.Error, "Give every independent Git service a unique SSH host source.");
        }

        foreach (var service in config.GitServices)
        {
            var defaultIdentity = string.IsNullOrWhiteSpace(service.DefaultIdentityId)
                ? null
                : config.Identities.FirstOrDefault(item => string.Equals(item.Id, service.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(service.DefaultIdentityId) && defaultIdentity is null)
            {
                Add(report, "SERVICE_DEFAULT_IDENTITY_MISSING", "Git services", $"Default identity: {service.DisplayName}",
                    $"Identity '{service.DefaultIdentityId}' does not exist.", DiagnosticSeverity.Error,
                    "Select an identity that exists and belongs to this service.");
            }
            else if (defaultIdentity is not null
                && !string.Equals(defaultIdentity.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
            {
                Add(report, "SERVICE_DEFAULT_IDENTITY_MISMATCH", "Git services", $"Default identity belongs to another service: {service.DisplayName}",
                    $"Identity '{defaultIdentity.DisplayName}' belongs to '{defaultIdentity.ServiceInstanceId}'.", DiagnosticSeverity.Error,
                    "Select a default identity from the same Git service.");
            }

            var serviceRoutes = config.RepositoryRoutes.Where(route => route.Enabled
                && route.Scope == GitRouteScope.Service
                && string.Equals(route.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (defaultIdentity is not null && serviceRoutes.Count == 0)
            {
                Add(report, "SERVICE_DEFAULT_ROUTE_MISSING", "Routes", $"Default identity has no service route: {service.DisplayName}",
                    $"Default identity: {defaultIdentity.DisplayName} ({defaultIdentity.HostAlias})", DiagnosticSeverity.Error,
                    "Save the service again to recreate its managed service-level route.");
            }
            else if (defaultIdentity is null && serviceRoutes.Count > 0)
            {
                Add(report, "SERVICE_ROUTE_WITHOUT_DEFAULT", "Routes", $"Service route has no default identity: {service.DisplayName}",
                    $"Enabled service routes: {serviceRoutes.Count}", DiagnosticSeverity.Error,
                    "Save the service after selecting a default identity, or remove the stale service-level route.");
            }

            if (service.ProviderKind == GitProviderKind.Gitea)
            {
                Add(report, $"GITEA_SSH_USER_{service.Id}", "Git services", $"Gitea SSH user: {service.DisplayName}",
                    $"SSH User is '{service.SshUser}'. In Gitea, 'git' is the shared SSH service account; the real web user is identified by the submitted public key, not by AccountName.",
                    string.Equals(service.SshUser, "git", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Normal : DiagnosticSeverity.Warning,
                    string.Equals(service.SshUser, "git", StringComparison.OrdinalIgnoreCase) ? null : "Use SSH User 'git' unless this Gitea instance is explicitly configured otherwise.");
            }

            if ((service.SshPort ?? 22) == 22)
            {
                Add(report, $"SSH_DEFAULT_PORT_{service.Id}", "Git services", $"Default SSH port: {service.DisplayName}",
                    "Port 22 is used. SCP-style clone URLs remain git@host:owner/repository.git and require no explicit port.", DiagnosticSeverity.Normal);
            }
        }

        foreach (var duplicate in config.RepositoryRoutes.Where(item => item.Enabled)
                     .GroupBy(item => $"{item.ServiceInstanceId}\n{item.Scope}\n{item.RoutePath}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var route = duplicate.First();
            Add(report, $"ROUTE_{route.Scope.ToString().ToUpperInvariant()}_DUPLICATE", "Routes", $"Duplicate {route.Scope} route",
                $"{route.ServiceInstanceId}/{route.DisplayPath}", DiagnosticSeverity.Error,
                "Keep only one enabled route for each service, scope, and route path.");
        }

        foreach (var serviceGroup in config.RepositoryRoutes.Where(item => item.Enabled)
                     .GroupBy(item => item.ServiceInstanceId, StringComparer.OrdinalIgnoreCase))
        {
            var serviceRoute = serviceGroup.FirstOrDefault(item => item.Scope == GitRouteScope.Service);
            var overrides = serviceGroup.Where(item => item.Scope != GitRouteScope.Service
                    && !IsLegacyAccountOwnerRoute(config, item))
                .ToList();
            if (serviceRoute is not null && overrides.Count > 0)
            {
                Add(report, "ROUTE_SCOPE_COVERAGE", "Routes", "Scoped routes override a service default",
                    $"Service: {serviceGroup.Key}{Environment.NewLine}Service identity: {serviceRoute.IdentityId}{Environment.NewLine}Overrides: {string.Join(", ", overrides.Select(item => $"{item.Scope}:{item.DisplayPath}"))}",
                    DiagnosticSeverity.Normal,
                    "Git uses the longest matching insteadOf prefix: Repository overrides Owner, and Owner overrides Service.");
            }
        }

        foreach (var service in config.GitServices.Where(item => item.ProviderKind == GitProviderKind.Gitea
                     && !string.IsNullOrWhiteSpace(item.DefaultIdentityId)))
        {
            var identity = config.Identities.FirstOrDefault(item => string.Equals(item.Id, service.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
            if (identity is null)
            {
                continue;
            }

            foreach (var route in config.RepositoryRoutes.Where(item => IsLegacyAccountOwnerRoute(config, item)
                         && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Add(report, "LEGACY_ACCOUNT_AS_OWNER_ROUTE", "Routes", "Legacy account-as-Owner route",
                    $"Service: {service.DisplayName}{Environment.NewLine}AccountName/Owner: {identity.AccountName}{Environment.NewLine}HostAlias: {identity.HostAlias}",
                    DiagnosticSeverity.Warning,
                    "Preview 'Convert legacy account route' and replace it with a service-level Gitea route after confirmation.");
            }
        }
    }

    private static bool IsLegacyAccountOwnerRoute(AppConfig config, RepositoryRoute route)
    {
        if (!route.Enabled || route.Scope != GitRouteScope.Owner)
        {
            return false;
        }

        var service = config.FindService(route.ServiceInstanceId);
        if (service?.ProviderKind != GitProviderKind.Gitea
            || string.IsNullOrWhiteSpace(service.DefaultIdentityId)
            || !string.Equals(route.IdentityId, service.DefaultIdentityId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var identity = config.Identities.FirstOrDefault(item =>
            string.Equals(item.Id, route.IdentityId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase));
        return identity is not null
            && string.Equals(route.Owner ?? route.NamespacePath, identity.AccountName, StringComparison.OrdinalIgnoreCase);
    }

    private void AddEnvironmentItems(DiagnosticReport report)
    {
        Add(report, "WINDOWS_USER", "Environment", "Current Windows user", Environment.UserName, DiagnosticSeverity.Normal);
        Add(report, "USER_HOME", "Environment", "User home directory", _paths.UserProfileDirectory, DiagnosticSeverity.Normal);
        Add(report, "SSH_DIRECTORY", "Environment", ".ssh directory",
            $"{_paths.SshDirectory} (exists: {_fileSystem.DirectoryExists(_paths.SshDirectory)})", DiagnosticSeverity.Normal);
        Add(report, "SSH_CONFIG_PATH", "Environment", "SSH config path",
            $"{_paths.SshConfigPath} (exists: {_fileSystem.FileExists(_paths.SshConfigPath)})", DiagnosticSeverity.Normal);
    }

    private static void AddTool(DiagnosticReport report, ExecutableInfo tool)
    {
        if (!tool.Exists)
        {
            Add(report, $"TOOL_{tool.Name}_MISSING", "Tools", tool.Name,
                "Not found.", DiagnosticSeverity.Error,
                "Use Overview > Detect/install required software, then run diagnostics again.");
            return;
        }

        Add(report, $"TOOL_{tool.Name}_OK", "Tools", tool.Name,
            $"Path: {tool.SelectedPath}{Environment.NewLine}Version: {tool.Version ?? "Unknown"}", DiagnosticSeverity.Normal);
        if (tool.CandidatePaths.Count > 1)
        {
            Add(report, $"TOOL_{tool.Name}_MULTIPLE", "Tools", $"Multiple {tool.Name} candidates",
                string.Join(Environment.NewLine, tool.CandidatePaths), DiagnosticSeverity.Warning,
                "Confirm that the selected executable is the version you intend to use.");
        }
    }

    private async Task AddIdentityChecksAsync(DiagnosticReport report, AppConfig config, CancellationToken cancellationToken)
    {
        foreach (var duplicate in config.Identities.GroupBy(item => item.HostAlias, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            Add(report, "HOST_ALIAS_DUPLICATE", "Identities", "Duplicate HostAlias", duplicate.Key,
                DiagnosticSeverity.Error, "Assign a unique HostAlias to every identity.");
        }

        foreach (var duplicate in config.Identities
                     .Where(item => !string.IsNullOrWhiteSpace(item.PrivateKeyPath))
                     .GroupBy(item => NormalizePath(item.PrivateKeyPath), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var sameServiceDifferentAccounts = duplicate
                .GroupBy(item => item.ServiceInstanceId, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(item => item.AccountName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            Add(report, sameServiceDifferentAccounts ? "PRIVATE_KEY_SAME_SERVICE_ACCOUNTS" : "PRIVATE_KEY_PATH_SHARED", "Keys", "Private key path is shared by multiple identities",
                $"Path: {duplicate.Key}{Environment.NewLine}Identities: {string.Join(", ", duplicate.Select(item => item.DisplayName))}",
                sameServiceDifferentAccounts ? DiagnosticSeverity.Error : DiagnosticSeverity.Normal,
                sameServiceDifferentAccounts
                    ? "Do not assign the same key to different accounts in one Git service."
                    : "This is allowed when independent services intentionally trust the same key pair.");
        }

        foreach (var duplicate in config.Identities
                     .Where(item => !string.IsNullOrWhiteSpace(item.PublicKeyPath))
                     .GroupBy(item => NormalizePath(item.PublicKeyPath), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var sameServiceDifferentAccounts = duplicate
                .GroupBy(item => item.ServiceInstanceId, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(item => item.AccountName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            Add(report, sameServiceDifferentAccounts ? "PUBLIC_KEY_SAME_SERVICE_ACCOUNTS" : "PUBLIC_KEY_PATH_SHARED", "Keys", "Public key path is shared by multiple identities",
                $"Path: {duplicate.Key}{Environment.NewLine}Identities: {string.Join(", ", duplicate.Select(item => item.DisplayName))}",
                sameServiceDifferentAccounts ? DiagnosticSeverity.Error : DiagnosticSeverity.Normal,
                sameServiceDifferentAccounts
                    ? "Do not assign the same public key to different accounts in one Git service."
                    : "This is an informational shared-key relationship across independent services.");
        }

        var publicKeyMaterial = new Dictionary<string, List<(GitIdentity Identity, string Path)>>(StringComparer.Ordinal);

        foreach (var identity in config.Identities)
        {
            Add(report, $"PRIVATE_KEY_{identity.Id}", "Keys", $"Private key: {identity.DisplayName}",
                $"{identity.PrivateKeyPath} (exists: {_fileSystem.FileExists(identity.PrivateKeyPath)})",
                _fileSystem.FileExists(identity.PrivateKeyPath) ? DiagnosticSeverity.Normal : DiagnosticSeverity.Error,
                _fileSystem.FileExists(identity.PrivateKeyPath) ? null : "Generate a key or select the correct private key path.");
            Add(report, $"PUBLIC_KEY_{identity.Id}", "Keys", $"Public key: {identity.DisplayName}",
                $"{identity.PublicKeyPath} (exists: {_fileSystem.FileExists(identity.PublicKeyPath)})",
                _fileSystem.FileExists(identity.PublicKeyPath) ? DiagnosticSeverity.Normal : DiagnosticSeverity.Error,
                _fileSystem.FileExists(identity.PublicKeyPath) ? null : "Generate or import the corresponding public key.");
            if (_fileSystem.FileExists(identity.PublicKeyPath))
            {
                try
                {
                    var publicKeyText = (await _fileSystem.ReadAllTextAsync(identity.PublicKeyPath, cancellationToken).ConfigureAwait(false)).Trim();
                    var looksLikePublicKey = SshKeyFormatDetector.TryNormalizeOpenSshPublicKey(
                        publicKeyText,
                        out var normalizedPublicKey,
                        out _);
                    if (looksLikePublicKey)
                    {
                        var keyParts = normalizedPublicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        var keyMaterial = string.Join(' ', keyParts.Take(2));
                        if (!publicKeyMaterial.TryGetValue(keyMaterial, out var usages))
                        {
                            usages = [];
                            publicKeyMaterial[keyMaterial] = usages;
                        }

                        usages.Add((identity, identity.PublicKeyPath));
                    }

                    Add(report, $"PUBLIC_KEY_READ_{identity.Id}", "Keys", $"Public key readable: {identity.DisplayName}",
                        looksLikePublicKey ? "The file is readable and has an OpenSSH public-key prefix." : "The file is readable but does not look like a normal OpenSSH public key.",
                        looksLikePublicKey ? DiagnosticSeverity.Normal : DiagnosticSeverity.Warning,
                        looksLikePublicKey ? null : "Confirm that PublicKeyPath points to the .pub file, not the private key.");
                }
                catch (Exception exception)
                {
                    Add(report, $"PUBLIC_KEY_READ_{identity.Id}", "Keys", $"Public key unreadable: {identity.DisplayName}",
                        exception.Message, DiagnosticSeverity.Error, "Check file access and select the correct public key path.");
                }
            }
        }

        foreach (var duplicate in publicKeyMaterial.Values.Where(group =>
                     group.Select(item => item.Identity.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                     && group.Select(item => NormalizePath(item.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var sameServiceDifferentAccounts = duplicate
                .GroupBy(item => item.Identity.ServiceInstanceId, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(item => item.Identity.AccountName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            Add(report, sameServiceDifferentAccounts ? "PUBLIC_KEY_MATERIAL_SAME_SERVICE_ACCOUNTS" : "PUBLIC_KEY_MATERIAL_REUSED", "Keys", "Different files contain the same public key",
                string.Join(Environment.NewLine, duplicate.Select(item => $"{item.Identity.DisplayName}: {item.Path}")),
                sameServiceDifferentAccounts ? DiagnosticSeverity.Error : DiagnosticSeverity.Normal,
                sameServiceDifferentAccounts
                    ? "Generate separate keys for different accounts in the same Git service."
                    : "Copies of one key across independent services are allowed when intentional.");
        }

        foreach (var group in config.RepositoryRoutes.Where(item => item.Enabled)
                     .GroupBy(
                         item => $"{item.ServiceInstanceId}\n{item.Scope}\n{item.RoutePath}",
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(item => item.IdentityId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var route = group.First();
            Add(report, "NAMESPACE_MULTIPLE_IDENTITIES", "Routes", "Namespace points to multiple identities",
                $"{route.ServiceInstanceId}/{route.DisplayPath}", DiagnosticSeverity.Error,
                "Keep only one enabled identity route for each service scope and path.");
        }

        foreach (var identity in config.Identities)
        {
            if (config.FindService(identity.ServiceInstanceId) is null)
            {
                Add(report, "IDENTITY_SERVICE_MISSING", "Identities", $"Identity: {identity.DisplayName}",
                    $"Git service '{identity.ServiceInstanceId}' does not exist.", DiagnosticSeverity.Error,
                    "Select an existing Git service for the identity.");
            }
        }

        foreach (var route in config.RepositoryRoutes.Where(item => item.Enabled))
        {
            var service = config.FindService(route.ServiceInstanceId);
            if (service is null)
            {
                Add(report, "ROUTE_SERVICE_MISSING", "Routes", $"Repository route: {route.NamespacePath}",
                    $"Git service '{route.ServiceInstanceId}' does not exist.", DiagnosticSeverity.Error,
                    "Select an existing Git service or disable the route.");
                continue;
            }

            var identity = config.Identities.FirstOrDefault(item =>
                string.Equals(item.Id, route.IdentityId, StringComparison.OrdinalIgnoreCase));
            if (identity is null)
            {
                Add(report, "ROUTE_IDENTITY_MISSING", "Routes", $"Repository route: {route.NamespacePath}",
                    "The referenced identity does not exist.", DiagnosticSeverity.Error,
                    "Select an existing identity or disable the route.");
            }
            else if (!string.Equals(identity.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
            {
                Add(report, "ROUTE_IDENTITY_SERVICE_MISMATCH", "Routes", $"Repository route: {route.NamespacePath}",
                    $"Identity '{identity.DisplayName}' belongs to '{identity.ServiceInstanceId}', not '{service.Id}'.",
                    DiagnosticSeverity.Error, "Select an identity from the same Git service.");
            }
        }
    }

    private async Task AddSshConfigChecksAsync(DiagnosticReport report, AppConfig config, CancellationToken cancellationToken)
    {
        var raw = await _sshConfigService.ReadRawAsync(cancellationToken).ConfigureAwait(false);
        var blocks = _sshConfigService.ParseManagedBlocks(raw);
        foreach (var orphan in blocks.Where(block => !config.Identities.Any(identity => string.Equals(identity.HostAlias, block.HostAlias, StringComparison.OrdinalIgnoreCase))))
        {
            Add(report, "SSH_BLOCK_ORPHAN", "SSH Config", "Managed block has no identity", orphan.HostAlias,
                DiagnosticSeverity.Warning, "Review the block and remove it through SSH Config if it is no longer needed.");
        }
        foreach (var duplicate in blocks.GroupBy(item => item.HostAlias, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            Add(report, "SSH_BLOCK_DUPLICATE", "SSH Config", "Duplicate managed block", duplicate.Key,
                DiagnosticSeverity.Error, "Remove the duplicate managed block before applying changes.");
        }

        var unmanaged = RemoveManagedBlocks(raw, blocks);
        foreach (var identity in config.Identities)
        {
            var managedCount = blocks.Count(item => string.Equals(item.HostAlias, identity.HostAlias, StringComparison.OrdinalIgnoreCase));
            Add(report, $"SSH_BLOCK_{identity.Id}", "SSH Config", $"Managed Host: {identity.HostAlias}",
                $"Matching managed blocks: {managedCount}", managedCount == 1 ? DiagnosticSeverity.Normal : DiagnosticSeverity.Error,
                managedCount == 1 ? null : "Use SSH Config > Sync identity after reviewing the diff.");

            var service = config.FindService(identity.ServiceInstanceId);
            var managedBlock = blocks.FirstOrDefault(item =>
                string.Equals(item.HostAlias, identity.HostAlias, StringComparison.OrdinalIgnoreCase));
            if (service is not null && managedCount == 1 && managedBlock is not null)
            {
                var expected = _providers.Get(service.ProviderKind)
                    .BuildSshManagedBlock(service, identity, DetectNewline(managedBlock.RawText));
                if (!NormalizeNewlines(managedBlock.RawText).Equals(NormalizeNewlines(expected), StringComparison.Ordinal))
                {
                    var blockDiff = TextDiffService.CreateSimpleDiff(
                        managedBlock.RawText,
                        expected,
                        $"current/{identity.HostAlias}",
                        $"expected/{identity.HostAlias}");
                    Add(report, "SSH_BLOCK_SERVICE_MISMATCH", "SSH Config", $"Managed Host differs: {identity.HostAlias}",
                        $"Current managed block:{Environment.NewLine}{managedBlock.RawText.TrimEnd()}{Environment.NewLine}{Environment.NewLine}Expected managed block:{Environment.NewLine}{expected.TrimEnd()}{Environment.NewLine}{Environment.NewLine}Diff:{Environment.NewLine}{blockDiff.TrimEnd()}",
                        DiagnosticSeverity.Error, "Synchronize the identity SSH block after reviewing the diff.");
                }
            }

            if (ContainsHost(unmanaged, identity.HostAlias))
            {
                Add(report, "SSH_UNMANAGED_HOST_CONFLICT", "SSH Config", "Unmanaged HostAlias conflict", identity.HostAlias,
                    DiagnosticSeverity.Warning,
                    "Rename the identity HostAlias or manually resolve the existing unmanaged SSH Host entry.");
            }
        }
    }

    private async Task AddGitChecksAsync(DiagnosticReport report, AppConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var originsResult = await _gitUrlRewriteService.GetGlobalConfigOriginsAsync(cancellationToken).ConfigureAwait(false);
            if (originsResult.Success)
            {
                var origins = originsResult.Value ?? [];
                Add(report, "GIT_GLOBAL_CONFIG_PATH", "Environment", "Git global configuration path",
                    origins.Count == 0 ? "No populated global Git config origin was reported by git.exe." : string.Join(Environment.NewLine, origins),
                    origins.Count == 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Normal,
                    origins.Count == 0 ? "This can be normal before the first global Git setting is written." : null);
            }
            else
            {
                Add(report, "GIT_GLOBAL_CONFIG_PATH_FAILED", "Environment", "Git global configuration path",
                    string.Join(Environment.NewLine, originsResult.Errors), DiagnosticSeverity.Warning,
                    "Verify git.exe and run diagnostics again.");
            }

            var actualRules = await _gitUrlRewriteService.GetActualRulesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var conflict in actualRules.GroupBy(item => item.InsteadOfUrl, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Select(item => item.BaseUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            {
                Add(report, "GIT_REWRITE_SOURCE_MULTIPLE_IDENTITIES", "Git URL rewrite", "One source prefix routes to multiple identities",
                    $"Source: {conflict.Key}{Environment.NewLine}Targets: {string.Join(", ", conflict.Select(item => item.BaseUrl).Distinct(StringComparer.OrdinalIgnoreCase))}",
                    DiagnosticSeverity.Error, "Keep exactly one target for each source prefix.");
            }

            foreach (var rule in actualRules)
            {
                var sourceService = config.GitServices.FirstOrDefault(service =>
                    _providers.Get(service.ProviderKind).GetSupportedRemotePatterns(service)
                        .Any(pattern => rule.InsteadOfUrl.StartsWith(pattern.Prefix, StringComparison.OrdinalIgnoreCase)));
                var targetAlias = ExtractHostAlias(rule.BaseUrl);
                var targetIdentity = config.Identities.FirstOrDefault(identity =>
                    string.Equals(identity.HostAlias, targetAlias, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(targetAlias) && targetIdentity is null)
                {
                    Add(report, "GIT_REWRITE_TARGET_ALIAS_MISSING", "Git URL rewrite", "Rewrite target HostAlias does not exist",
                        $"Source: {rule.InsteadOfUrl}{Environment.NewLine}Target Base: {rule.BaseUrl}{Environment.NewLine}HostAlias: {targetAlias}",
                        DiagnosticSeverity.Error,
                        "Create the matching managed identity or remove the stale rewrite after reviewing the diff.");
                }

                if (sourceService is not null && targetIdentity is not null
                    && !string.Equals(sourceService.Id, targetIdentity.ServiceInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    Add(report, "GIT_REWRITE_CROSS_SERVICE", "Git URL rewrite", "Git service URL is routed to another service identity",
                        $"Source service: {sourceService.DisplayName}{Environment.NewLine}Source: {rule.InsteadOfUrl}{Environment.NewLine}Target HostAlias: {targetIdentity.HostAlias} ({targetIdentity.ServiceInstanceId})",
                        DiagnosticSeverity.Error, "Remove the cross-instance rewrite and generate rules from the matching Git service only.");
                }
            }

            var legacyPlan = await _gitUrlRewriteService.BuildLegacyAccountOwnerMigrationPlanAsync(cancellationToken).ConfigureAwait(false);
            if (legacyPlan.HasChanges)
            {
                Add(report, "LEGACY_ACCOUNT_AS_OWNER_GIT_CONFIG", "Git URL rewrite", "Legacy account-as-Owner Git config detected",
                    $"Rules to remove: {legacyPlan.Removes.Count}{Environment.NewLine}Service-level rules to add: {legacyPlan.Adds.Count}",
                    DiagnosticSeverity.Warning,
                    "Preview the migration diff and confirm conversion; diagnostics never remove these rules automatically.");
            }

            var comparisons = await _gitUrlRewriteService.CompareAsync(cancellationToken).ConfigureAwait(false);
            foreach (var comparison in comparisons)
            {
                var severity = comparison.Status switch
                {
                    GitRewriteStatus.Correct => DiagnosticSeverity.Normal,
                    GitRewriteStatus.LegacyAccountOwner => DiagnosticSeverity.Warning,
                    GitRewriteStatus.Extra => DiagnosticSeverity.Warning,
                    GitRewriteStatus.Missing => DiagnosticSeverity.Error,
                    GitRewriteStatus.Duplicate => DiagnosticSeverity.Error,
                    GitRewriteStatus.Conflict => DiagnosticSeverity.Error,
                    _ => DiagnosticSeverity.Warning
                };
                var legacy = comparison.Status == GitRewriteStatus.LegacyAccountOwner;
                Add(report, $"GIT_REWRITE_{comparison.Status}", "Git URL rewrite",
                    legacy
                        ? "Legacy account-as-Owner route"
                        : comparison.NamespacePath is null
                        ? comparison.InsteadOfUrl
                        : $"{comparison.ServiceInstanceId}/{comparison.NamespacePath}",
                    $"Status: {(legacy ? "Legacy account-as-owner route" : comparison.Status)}{Environment.NewLine}Base: {comparison.ExpectedBaseUrl}{Environment.NewLine}insteadOf: {comparison.InsteadOfUrl}{Environment.NewLine}Matches: {comparison.ActualMatchCount}",
                    severity,
                    legacy
                        ? "Convert this Gitea login-account route to the service-level default route after reviewing the migration diff."
                        : severity == DiagnosticSeverity.Normal ? null : "Review the Git rewrite diff before applying a repair.");
            }
        }
        catch (Exception exception)
        {
            Add(report, "GIT_REWRITE_READ_FAILED", "Git URL rewrite", "Unable to read Git URL rewrites",
                exception.Message, DiagnosticSeverity.Error, "Verify git.exe and the global Git configuration, then run diagnostics again.");
        }
    }

    private static string RemoveManagedBlocks(string raw, IReadOnlyList<SshManagedBlock> blocks)
    {
        var result = raw;
        foreach (var block in blocks.OrderByDescending(item => item.StartIndex))
        {
            result = result.Remove(block.StartIndex, block.Length);
        }

        return result;
    }

    private static bool ContainsHost(string text, string hostAlias)
        => Regex.IsMatch(text, $"(?im)^\\s*Host\\s+{Regex.Escape(hostAlias)}(?:\\s|$)", RegexOptions.CultureInvariant);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string DetectNewline(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    private static string? ExtractHostAlias(string baseUrl)
    {
        var at = baseUrl.IndexOf('@');
        if (at < 0 || at == baseUrl.Length - 1)
        {
            return null;
        }

        var end = baseUrl.IndexOfAny([':', '/'], at + 1);
        return end < 0 ? baseUrl[(at + 1)..] : baseUrl[(at + 1)..end];
    }

    private static void Add(
        DiagnosticReport report,
        string code,
        string category,
        string title,
        string message,
        DiagnosticSeverity severity,
        string? action = null)
        => report.Items.Add(new DiagnosticItem
        {
            Code = code,
            Category = category,
            Title = title,
            Message = message,
            Severity = severity,
            SuggestedAction = action
        });
}
