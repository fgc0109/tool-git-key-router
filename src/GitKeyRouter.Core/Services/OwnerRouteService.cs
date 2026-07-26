using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed class OwnerRouteService
{
    private readonly IAppConfigStore _configStore;
    private readonly IBackupService _backupService;
    private readonly GitProviderAdapterRegistry _providers;

    public OwnerRouteService(
        IAppConfigStore configStore,
        IBackupService backupService,
        GitProviderAdapterRegistry? providers = null)
    {
        _configStore = configStore;
        _backupService = backupService;
        _providers = providers ?? GitProviderAdapterRegistry.CreateDefault();
    }

    public async Task<IReadOnlyList<RepositoryRoute>> ListAsync(CancellationToken cancellationToken = default)
        => (await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false)).RepositoryRoutes
            .OrderBy(item => item.ServiceInstanceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.NamespacePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<OperationResult<RepositoryRoute>> SaveAsync(
        RepositoryRoute route,
        string? originalOwner = null,
        CancellationToken cancellationToken = default)
        => await SaveAsync(
            route,
            route.ServiceInstanceId,
            originalOwner,
            cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult<RepositoryRoute>> SaveAsync(
        RepositoryRoute route,
        string? originalServiceInstanceId,
        string? originalNamespacePath,
        CancellationToken cancellationToken = default)
    {
        route.Normalize();
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var validation = OwnerRouteValidator.Validate(
            route,
            config,
            originalServiceInstanceId,
            originalNamespacePath,
            _providers);
        if (!validation.IsValid)
        {
            return OperationResult<RepositoryRoute>.Fail("Repository route validation failed.", validation.Errors.ToArray());
        }

        await _backupService.CreateSnapshotAsync(
            $"Save repository route: {route.ServiceInstanceId}/{route.NamespacePath}",
            cancellationToken).ConfigureAwait(false);
        var existing = config.RepositoryRoutes.FirstOrDefault(item =>
            string.Equals(item.Id, route.Id, StringComparison.OrdinalIgnoreCase))
            ?? config.RepositoryRoutes.FirstOrDefault(item =>
                string.Equals(item.ServiceInstanceId, originalServiceInstanceId ?? route.ServiceInstanceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.NamespacePath, originalNamespacePath ?? route.NamespacePath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            config.RepositoryRoutes.Add(route);
        }
        else
        {
            existing.ServiceInstanceId = route.ServiceInstanceId;
            existing.Scope = route.Scope;
            existing.Owner = route.Owner;
            existing.Repository = route.Repository;
            existing.NamespacePath = route.NamespacePath;
            existing.IdentityId = route.IdentityId;
            existing.Enabled = route.Enabled;
        }

        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult<RepositoryRoute>.Ok(route, "Repository route saved.");
    }

    public async Task<OperationResult> DeleteAsync(string owner, CancellationToken cancellationToken = default)
        => await DeleteAsync(GitServiceInstance.GitHubComId, owner, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> DeleteAsync(
        string serviceInstanceId,
        string namespacePath,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var route = config.RepositoryRoutes.FirstOrDefault(item =>
            string.Equals(item.ServiceInstanceId, serviceInstanceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.NamespacePath, namespacePath, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            return OperationResult.Fail("Repository route was not found.");
        }

        await _backupService.CreateSnapshotAsync(
            $"Delete repository route: {serviceInstanceId}/{namespacePath}",
            cancellationToken).ConfigureAwait(false);
        config.RepositoryRoutes.Remove(route);
        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Repository route deleted from application configuration.");
    }

    public async Task<OperationResult> DeleteByIdAsync(string routeId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var route = config.RepositoryRoutes.FirstOrDefault(item =>
            string.Equals(item.Id, routeId, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            return OperationResult.Fail("Repository route was not found.");
        }

        await _backupService.CreateSnapshotAsync(
            $"Delete repository route: {route.ServiceInstanceId}/{route.DisplayPath}",
            cancellationToken).ConfigureAwait(false);
        config.RepositoryRoutes.Remove(route);
        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Repository route deleted from application configuration.");
    }

    public static IReadOnlyList<GitUrlRewriteRule> BuildExpectedRules(
        AppConfig config,
        GitProviderAdapterRegistry? providers = null)
    {
        providers ??= GitProviderAdapterRegistry.CreateDefault();
        config.Normalize();
        var identities = config.Identities.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var rules = new List<GitUrlRewriteRule>();
        foreach (var route in config.RepositoryRoutes.Where(item => item.Enabled))
        {
            route.Normalize();
            if (!identities.TryGetValue(route.IdentityId, out var identity))
            {
                continue;
            }

            var service = config.FindService(route.ServiceInstanceId);
            if (service is null
                || !string.Equals(identity.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rules.AddRange(providers.Get(service.ProviderKind).BuildRewriteRules(service, identity, route));
        }

        return rules;
    }
}
