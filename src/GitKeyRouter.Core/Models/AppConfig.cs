using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace GitKeyRouter.Core.Models;

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string UiLanguage { get; set; } = "zh-CN";

    public List<GitServiceInstance> GitServices { get; set; } = [GitServiceInstance.CreateGitHubCom()];

    public List<GitIdentity> Identities { get; set; } = [];

    public List<RepositoryRoute> RepositoryRoutes { get; set; } = [];

    public List<GitProfile> GitProfiles { get; set; } = [];

    public List<GitProfileRule> GitProfileRules { get; set; } = [];

    [JsonIgnore]
    public List<RepositoryRoute> OwnerRoutes
    {
        get => RepositoryRoutes;
        set => RepositoryRoutes = value ?? [];
    }

    public GitServiceInstance? FindService(string serviceInstanceId)
        => GitServices.FirstOrDefault(item => string.Equals(item.Id, serviceInstanceId, StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        var sourceSchemaVersion = SchemaVersion;
        SchemaVersion = CurrentSchemaVersion;
        GitServices ??= [];
        Identities ??= [];
        RepositoryRoutes ??= [];
        GitProfiles ??= [];
        GitProfileRules ??= [];
        UiLanguage = UiLanguage?.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? "en-US"
            : "zh-CN";

        if (!GitServices.Any(item => string.Equals(item.Id, GitServiceInstance.GitHubComId, StringComparison.OrdinalIgnoreCase)))
        {
            GitServices.Insert(0, GitServiceInstance.CreateGitHubCom());
        }

        foreach (var identity in Identities)
        {
            if (string.IsNullOrWhiteSpace(identity.ServiceInstanceId))
            {
                identity.ServiceInstanceId = GitServiceInstance.GitHubComId;
            }
        }

        foreach (var service in GitServices)
        {
            service.HostName = service.HostName.Trim();
            service.WebBaseUrl = service.WebBaseUrl.TrimEnd('/');
            service.SshPort ??= 22;
        }

        foreach (var route in RepositoryRoutes)
        {
            var requiresStableMigrationId = sourceSchemaVersion < 4 && string.IsNullOrWhiteSpace(route.Id);
            if (string.IsNullOrWhiteSpace(route.ServiceInstanceId))
            {
                route.ServiceInstanceId = GitServiceInstance.GitHubComId;
            }

            route.Normalize();
            if (requiresStableMigrationId)
            {
                route.Id = CreateMigratedRouteId(route);
            }
        }

        if (sourceSchemaVersion < 4)
        {
            InferLegacyGiteaDefaultIdentities();
        }

        SynchronizeDefaultServiceRoutes();
    }

    private void InferLegacyGiteaDefaultIdentities()
    {
        foreach (var service in GitServices.Where(item =>
                     item.ProviderKind == GitProviderKind.Gitea
                     && string.IsNullOrWhiteSpace(item.DefaultIdentityId)))
        {
            var candidates = RepositoryRoutes
                .Where(route => route.Enabled
                    && route.Scope == GitRouteScope.Owner
                    && string.Equals(route.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
                .Select(route => Identities.FirstOrDefault(identity =>
                    string.Equals(identity.Id, route.IdentityId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(identity.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(identity.AccountName, route.Owner ?? route.NamespacePath, StringComparison.OrdinalIgnoreCase)))
                .Where(identity => identity is not null)
                .Select(identity => identity!.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 1)
            {
                service.DefaultIdentityId = candidates[0];
            }
        }
    }

    private static string CreateMigratedRouteId(RepositoryRoute route)
    {
        var descriptor = string.Join(
            "\n",
            route.ServiceInstanceId,
            route.IdentityId,
            route.Scope,
            route.RoutePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor));
        return $"migrated-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    public void SynchronizeDefaultServiceRoutes()
    {
        foreach (var service in GitServices)
        {
            var managedRouteId = $"service-default:{service.Id}";
            var managedRoute = RepositoryRoutes.FirstOrDefault(item =>
                string.Equals(item.Id, managedRouteId, StringComparison.OrdinalIgnoreCase));
            var defaultIdentity = string.IsNullOrWhiteSpace(service.DefaultIdentityId)
                ? null
                : Identities.FirstOrDefault(item =>
                    string.Equals(item.Id, service.DefaultIdentityId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase));

            if (defaultIdentity is null)
            {
                if (managedRoute is not null)
                {
                    RepositoryRoutes.Remove(managedRoute);
                }

                continue;
            }

            var serviceRoute = managedRoute ?? RepositoryRoutes.FirstOrDefault(item =>
                item.Scope == GitRouteScope.Service
                && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase));
            if (serviceRoute is null)
            {
                serviceRoute = new RepositoryRoute();
                RepositoryRoutes.Add(serviceRoute);
            }

            serviceRoute.Id = managedRouteId;
            serviceRoute.ServiceInstanceId = service.Id;
            serviceRoute.IdentityId = defaultIdentity.Id;
            serviceRoute.Scope = GitRouteScope.Service;
            serviceRoute.Enabled = true;
            serviceRoute.Normalize();
        }
    }
}
