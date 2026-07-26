using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.Configuration;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public async Task CurrentSchemaWithLowercasePropertyPreservesCurrentConfigurationFields()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.AppDataDirectory);
        var json = $$"""
        {
          "schemaVersion": {{AppConfig.CurrentSchemaVersion}},
          "UiLanguage": "en-US",
          "GitServices": [
            {
              "Id": "gitea-cloud",
              "DisplayName": "Gitea Cloud",
              "ProviderKind": "Gitea",
              "HostName": "git.policoil.top",
              "SshPort": 2222,
              "SshUser": "forge",
              "WebBaseUrl": "https://git.policoil.top"
            }
          ],
          "Identities": [
            {
              "Id": "cloud",
              "ServiceInstanceId": "gitea-cloud",
              "DisplayName": "Cloud",
              "AccountName": "camus",
              "HostAlias": "gitea-cloud-camus",
              "PrivateKeyPath": "C:\\keys\\cloud",
              "PublicKeyPath": "C:\\keys\\cloud.pub"
            }
          ],
          "RepositoryRoutes": [
            {
              "Id": "cloud-owner",
              "ServiceInstanceId": "gitea-cloud",
              "IdentityId": "cloud",
              "Scope": "Owner",
              "NamespacePath": "project-base",
              "Enabled": true
            }
          ],
          "GitProfiles": [
            {
              "Id": "work",
              "DisplayName": "Work"
            }
          ],
          "GitProfileRules": []
        }
        """;
        await File.WriteAllTextAsync(paths.ConfigPath, json);

        var config = await new JsonAppConfigStore(paths, new PhysicalFileSystem()).LoadAsync();

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        var gitea = Assert.Single(config.GitServices, service => service.Id == "gitea-cloud");
        Assert.Equal(2222, gitea.SshPort);
        Assert.Equal("forge", gitea.SshUser);
        Assert.Equal("cloud", Assert.Single(config.Identities).Id);
        Assert.Equal("project-base", Assert.Single(config.RepositoryRoutes).NamespacePath);
        Assert.Equal("work", Assert.Single(config.GitProfiles).Id);
    }

    [Fact]
    public async Task LowercaseFutureSchemaIsRejectedWithoutModifyingConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.AppDataDirectory);
        var json = $"{{\"schemaVersion\":{AppConfig.CurrentSchemaVersion + 1},\"GitServices\":[]}}";
        await File.WriteAllTextAsync(paths.ConfigPath, json);
        var store = new JsonAppConfigStore(paths, new PhysicalFileSystem());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.Contains("newer than the supported schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(json, await File.ReadAllTextAsync(paths.ConfigPath));
    }

    [Fact]
    public async Task Schema1Migration_PreservesGitHubSshAndRewriteOutputExactly()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.AppDataDirectory);
        var schema1 = """
        {
          "SchemaVersion": 1,
          "Identities": [
            {
              "Id": "camus",
              "DisplayName": "Camus GitHub",
              "GitHubUsername": "camus0109",
              "HostAlias": "github-camus",
              "PrivateKeyPath": "C:\\keys\\camus",
              "PublicKeyPath": "C:\\keys\\camus.pub",
              "EmailOrComment": "camus@example.com",
              "CreatedAt": "2026-07-18T00:00:00+00:00"
            }
          ],
          "OwnerRoutes": [
            {
              "GitHubOwner": "camus0109",
              "IdentityId": "camus",
              "Enabled": true
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(paths.ConfigPath, schema1);

        var store = new JsonAppConfigStore(paths, new PhysicalFileSystem());
        var config = await store.LoadAsync();

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        var service = Assert.Single(config.GitServices);
        Assert.Equal(GitServiceInstance.GitHubComId, service.Id);
        var identity = Assert.Single(config.Identities);
        Assert.Equal("camus0109", identity.AccountName);
        Assert.Equal(service.Id, identity.ServiceInstanceId);
        var route = Assert.Single(config.RepositoryRoutes);
        Assert.Equal("camus0109", route.NamespacePath);
        Assert.Equal(service.Id, route.ServiceInstanceId);

        const string newline = "\r\n";
        var expectedSsh = string.Join(newline,
        [
            "# BEGIN GitKeyRouter managed block: github-camus",
            "Host github-camus",
            "    HostName github.com",
            "    User git",
            "    IdentityFile C:/keys/camus",
            "    IdentitiesOnly yes",
            "# END GitKeyRouter managed block: github-camus",
            string.Empty
        ]);
        Assert.Equal(expectedSsh, SshConfigService.BuildManagedBlock(service, identity, newline));

        var rules = OwnerRouteService.BuildExpectedRules(config);
        Assert.Equal(
        [
            new GitUrlRewriteRule("git@github-camus:camus0109/", "https://github.com/camus0109/"),
            new GitUrlRewriteRule("git@github-camus:camus0109/", "git@github.com:camus0109/")
        ],
        rules);

        await store.SaveAsync(config);
        var saved = await File.ReadAllTextAsync(paths.ConfigPath);
        Assert.Contains($"\"SchemaVersion\": {AppConfig.CurrentSchemaVersion}", saved, StringComparison.Ordinal);
        Assert.Contains("\"GitServices\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"RepositoryRoutes\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"AccountName\": \"camus0109\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"OwnerRoutes\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"GitHubUsername\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema2Migration_PreservesServicesIdentitiesAndRoutesAndAddsEmptyProfiles()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.AppDataDirectory);
        var schema2 = """
        {
          "SchemaVersion": 2,
          "GitServices": [
            {
              "Id": "gitlab-office",
              "DisplayName": "Office GitLab",
              "ProviderKind": "GitLab",
              "HostName": "gitlab.office.example",
              "SshPort": 2222,
              "SshUser": "git",
              "WebBaseUrl": "https://gitlab.office.example",
              "IsBuiltIn": false
            }
          ],
          "Identities": [
            {
              "Id": "work",
              "ServiceInstanceId": "gitlab-office",
              "DisplayName": "Work",
              "AccountName": "camus",
              "HostAlias": "gitlab-work",
              "PrivateKeyPath": "C:\\keys\\work",
              "PublicKeyPath": "C:\\keys\\work.pub"
            }
          ],
          "RepositoryRoutes": [
            {
              "ServiceInstanceId": "gitlab-office",
              "NamespacePath": "company/platform",
              "IdentityId": "work",
              "Enabled": true
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(paths.ConfigPath, schema2);

        var store = new JsonAppConfigStore(paths, new PhysicalFileSystem());
        var config = await store.LoadAsync();

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.NotNull(config.FindService(GitServiceInstance.GitHubComId));
        var gitLab = Assert.Single(config.GitServices, item => item.Id == "gitlab-office");
        Assert.Equal(2222, gitLab.SshPort);
        Assert.Equal("work", Assert.Single(config.Identities).Id);
        Assert.Equal("company/platform", Assert.Single(config.RepositoryRoutes).NamespacePath);
        Assert.Empty(config.GitProfiles);
        Assert.Empty(config.GitProfileRules);
    }

    [Fact]
    public async Task Schema3Migration_InfersGiteaDefaultsAndExposesServiceRoutes()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.AppDataDirectory);
        var schema3 = """
        {
          "SchemaVersion": 3,
          "GitServices": [
            {
              "Id": "git.policoil.top",
              "DisplayName": "Gitea-Cloud",
              "ProviderKind": "Gitea",
              "HostName": "git.policoil.top",
              "SshUser": "git",
              "WebBaseUrl": "https://git.policoil.top"
            },
            {
              "Id": "gitea.lan.policoil.top",
              "DisplayName": "Gitea-Local",
              "ProviderKind": "Gitea",
              "HostName": "gitea.lan.policoil.top",
              "SshUser": "git",
              "WebBaseUrl": "https://gitea.lan.policoil.top"
            }
          ],
          "Identities": [
            {
              "Id": "cloud-identity",
              "ServiceInstanceId": "git.policoil.top",
              "DisplayName": "fgc0109",
              "AccountName": "fgc0109",
              "HostAlias": "gitea-cloud",
              "PrivateKeyPath": "C:\\keys\\shared",
              "PublicKeyPath": "C:\\keys\\shared.pub"
            },
            {
              "Id": "local-identity",
              "ServiceInstanceId": "gitea.lan.policoil.top",
              "DisplayName": "gitea-local",
              "AccountName": "fgc0109",
              "HostAlias": "gitea-local",
              "PrivateKeyPath": "C:\\keys\\shared",
              "PublicKeyPath": "C:\\keys\\shared.pub"
            }
          ],
          "RepositoryRoutes": [
            {
              "Id": "",
              "ServiceInstanceId": "git.policoil.top",
              "NamespacePath": "fgc0109",
              "IdentityId": "cloud-identity",
              "Enabled": true
            },
            {
              "Id": "",
              "ServiceInstanceId": "gitea.lan.policoil.top",
              "NamespacePath": "fgc0109",
              "IdentityId": "local-identity",
              "Enabled": true
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(paths.ConfigPath, schema3);

        var configStore = new JsonAppConfigStore(paths, new PhysicalFileSystem());
        var config = await configStore.LoadAsync();
        var cloud = Assert.Single(config.GitServices, item => item.Id == "git.policoil.top");
        var local = Assert.Single(config.GitServices, item => item.Id == "gitea.lan.policoil.top");

        Assert.Equal("cloud-identity", cloud.DefaultIdentityId);
        Assert.Equal("local-identity", local.DefaultIdentityId);
        var migratedRouteIds = config.RepositoryRoutes
            .Where(route => route.Scope == GitRouteScope.Owner)
            .Select(route => route.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, migratedRouteIds.Count);
        Assert.All(migratedRouteIds, id => Assert.StartsWith("migrated-", id, StringComparison.Ordinal));
        var reloadedRouteIds = (await configStore.LoadAsync()).RepositoryRoutes
            .Where(route => route.Scope == GitRouteScope.Owner)
            .Select(route => route.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(migratedRouteIds, reloadedRouteIds);
        Assert.Contains(config.RepositoryRoutes, route => route.Scope == GitRouteScope.Service && route.IdentityId == "cloud-identity");
        Assert.Contains(config.RepositoryRoutes, route => route.Scope == GitRouteScope.Service && route.IdentityId == "local-identity");

        var git = new FakeGitUrlRewriteStore();
        foreach (var legacyRoute in config.RepositoryRoutes.Where(route => route.Scope == GitRouteScope.Owner))
        {
            var service = config.FindService(legacyRoute.ServiceInstanceId)!;
            var identity = config.Identities.Single(item => item.Id == legacyRoute.IdentityId);
            git.Rules.AddRange(GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.Gitea)
                .BuildRewriteRules(service, identity, legacyRoute));
        }

        var rewrites = new GitUrlRewriteService(configStore, git, new NoOpBackupService());
        var preview = await rewrites.PreviewAsync("git@git.policoil.top:project-base/proto-tool-pb-extra.git");
        var comparisons = await rewrites.CompareAsync();
        var repair = await rewrites.BuildReconcilePlanAsync();

        Assert.Equal("git@git.policoil.top:", preview.ExpectedMatchedPrefix);
        Assert.Equal("git@gitea-cloud:", preview.ExpectedMatchedBaseUrl);
        Assert.Equal("git@gitea-cloud:project-base/proto-tool-pb-extra.git", preview.ExpectedRewrittenUrl);
        Assert.Equal(UrlRewriteExpectationStatus.Missing, preview.ExpectationStatus);
        Assert.Contains(comparisons, comparison => comparison.Status == GitRewriteStatus.LegacyAccountOwner
            && comparison.ExpectedBaseUrl == "git@gitea-cloud:fgc0109/");
        Assert.Contains(comparisons, comparison => comparison.Status == GitRewriteStatus.Missing
            && comparison.ExpectedBaseUrl == "git@gitea-cloud:");
        Assert.Equal(8, repair.Removes.Count);
        Assert.Equal(8, repair.Adds.Count);
        Assert.All(migratedRouteIds, id => Assert.Contains(id, repair.RepositoryRouteIdsToRemove));

        Assert.True((await rewrites.ApplyPlanAsync(repair, "confirm Schema 3 migration")).Success);
        var secondRepair = await rewrites.BuildReconcilePlanAsync();
        Assert.False(secondRepair.HasChanges);
        Assert.DoesNotContain(git.Rules, rule => rule.BaseUrl.Contains(":fgc0109/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(git.Rules, rule => rule.BaseUrl == "git@gitea-cloud:"
            && rule.InsteadOfUrl == "git@git.policoil.top:");
        Assert.Contains(git.Rules, rule => rule.BaseUrl == "git@gitea-local:"
            && rule.InsteadOfUrl == "git@gitea.lan.policoil.top:");
    }
}
