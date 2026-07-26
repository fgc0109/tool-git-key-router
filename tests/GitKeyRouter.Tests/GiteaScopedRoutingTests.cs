using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Core.Validation;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class GiteaScopedRoutingTests
{
    private const string SharedPrivateKey = @"C:\Users\fgc01\.ssh\id_ed25519_gitea";
    private const string SharedPublicKey = SharedPrivateKey + ".pub";

    [Theory]
    [InlineData("Gitea Local", "gitea-local", "gitea.lan.policoil.top", "https://gitea.lan.policoil.top/")]
    [InlineData("Gitea Cloud", "gitea-cloud", "git.policoil.top", "https://git.policoil.top/")]
    public void ServiceRoute_GeneratesScpSshGitSshAndHttpsPrefixes(string template, string alias, string host, string httpsPrefix)
    {
        var service = GitServiceService.CreateTemplate(template);
        var identity = Identity(alias, service.Id, alias);
        var route = Route(service, identity, GitRouteScope.Service);

        var rules = GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.Gitea)
            .BuildRewriteRules(service, identity, route);

        Assert.Equal(4, rules.Count);
        Assert.All(rules, rule => Assert.Equal($"git@{alias}:", rule.BaseUrl));
        Assert.Contains(rules, rule => rule.InsteadOfUrl == $"git@{host}:");
        Assert.Contains(rules, rule => rule.InsteadOfUrl == $"ssh://git@{host}/");
        Assert.Contains(rules, rule => rule.InsteadOfUrl == $"git+ssh://git@{host}/");
        Assert.Contains(rules, rule => rule.InsteadOfUrl == httpsPrefix);
        Assert.Equal(22, service.SshPort);
    }

    [Fact]
    public async Task DefaultGiteaIdentities_CreateOnlyServiceRoutesAndMayShareKeys()
    {
        var local = GitServiceService.CreateTemplate("Gitea Local");
        var cloud = GitServiceService.CreateTemplate("Gitea Cloud");
        var localIdentity = Identity("gitea-local", local.Id, "gitea-local");
        var cloudIdentity = Identity("gitea-cloud", cloud.Id, "gitea-cloud");
        local.DefaultIdentityId = localIdentity.Id;
        cloud.DefaultIdentityId = cloudIdentity.Id;
        var store = new InMemoryAppConfigStore
        {
            Config = new AppConfig { Identities = [localIdentity, cloudIdentity] }
        };
        var manager = CreateServiceManager(store);

        Assert.True((await manager.SaveAsync(local)).Success);
        Assert.True((await manager.SaveAsync(cloud)).Success);

        Assert.Equal(2, store.Config.RepositoryRoutes.Count);
        Assert.All(store.Config.RepositoryRoutes, route => Assert.Equal(GitRouteScope.Service, route.Scope));
        Assert.DoesNotContain(store.Config.RepositoryRoutes, route => route.Scope == GitRouteScope.Owner);
        Assert.DoesNotContain(store.Config.RepositoryRoutes, route => string.Equals(route.Owner, "fgc0109", StringComparison.OrdinalIgnoreCase));
        Assert.True(IdentityValidator.Validate(localIdentity, store.Config).IsValid);
        Assert.True(IdentityValidator.Validate(cloudIdentity, store.Config).IsValid);
    }

    [Fact]
    public void SharedGiteaKey_ProducesIndependentPort22SshBlocks()
    {
        var local = GitServiceService.CreateTemplate("Gitea Local");
        var cloud = GitServiceService.CreateTemplate("Gitea Cloud");
        var adapter = GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.Gitea);

        var localBlock = adapter.BuildSshManagedBlock(local, Identity("gitea-local", local.Id, "gitea-local"), "\n");
        var cloudBlock = adapter.BuildSshManagedBlock(cloud, Identity("gitea-cloud", cloud.Id, "gitea-cloud"), "\n");

        Assert.Contains("Host gitea-local\n", localBlock, StringComparison.Ordinal);
        Assert.Contains("HostName gitea.lan.policoil.top", localBlock, StringComparison.Ordinal);
        Assert.Contains("Host gitea-cloud\n", cloudBlock, StringComparison.Ordinal);
        Assert.Contains("HostName git.policoil.top", cloudBlock, StringComparison.Ordinal);
        Assert.Contains("User git", localBlock, StringComparison.Ordinal);
        Assert.Contains("Port 22", localBlock, StringComparison.Ordinal);
        Assert.Contains("Port 22", cloudBlock, StringComparison.Ordinal);
        Assert.Contains("IdentityFile C:/Users/fgc01/.ssh/id_ed25519_gitea", localBlock, StringComparison.Ordinal);
        Assert.Contains("IdentityFile C:/Users/fgc01/.ssh/id_ed25519_gitea", cloudBlock, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongestPrefix_UsesRepositoryThenOwnerThenService()
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var defaultIdentity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        var ownerIdentity = Identity("gitea-special", service.Id, "gitea-special");
        var repositoryIdentity = Identity("gitea-private", service.Id, "gitea-private");
        var routes = new[]
        {
            Route(service, defaultIdentity, GitRouteScope.Service),
            Route(service, ownerIdentity, GitRouteScope.Owner, "special-org"),
            Route(service, repositoryIdentity, GitRouteScope.Repository, "special-org", "private-repo.git")
        };
        var git = new FakeGitUrlRewriteStore();
        foreach (var route in routes)
        {
            var identity = route.IdentityId == defaultIdentity.Id ? defaultIdentity : route.IdentityId == ownerIdentity.Id ? ownerIdentity : repositoryIdentity;
            git.Rules.AddRange(GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.Gitea).BuildRewriteRules(service, identity, route));
        }

        var rewrites = new GitUrlRewriteService(new InMemoryAppConfigStore(), git, new NoOpBackupService());
        Assert.Equal("git@gitea-cloud:project-base/proto-tool-pb-extra.git",
            (await rewrites.PreviewAsync("https://git.policoil.top/project-base/proto-tool-pb-extra.git")).RewrittenUrl);
        Assert.Equal("git@gitea-special:special-org/server.git",
            (await rewrites.PreviewAsync("https://git.policoil.top/special-org/server.git")).RewrittenUrl);
        Assert.Equal("git@gitea-private:special-org/private-repo.git",
            (await rewrites.PreviewAsync("https://git.policoil.top/special-org/private-repo.git")).RewrittenUrl);
    }

    [Fact]
    public async Task GitHub_DefaultIdentityFallsBackBehindOwnerAndRepositoryRoutes()
    {
        var github = GitServiceInstance.CreateGitHubCom();
        var fallback = Identity("github-default", github.Id, "github-default", "default-user");
        var owner = Identity("github-owner", github.Id, "github-owner", "special-org");
        var repository = Identity("github-repository", github.Id, "github-repository", "special-org");
        github.DefaultIdentityId = fallback.Id;
        var config = new AppConfig
        {
            GitServices = [github],
            Identities = [fallback, owner, repository],
            RepositoryRoutes =
            [
                Route(github, owner, GitRouteScope.Owner, "special-org"),
                Route(github, repository, GitRouteScope.Repository, "special-org", "private-repo.git")
            ]
        };
        config.Normalize();
        var serviceRoute = Assert.Single(config.RepositoryRoutes, route => route.Scope == GitRouteScope.Service);
        var rules = OwnerRouteService.BuildExpectedRules(config);
        var git = new FakeGitUrlRewriteStore();
        git.Rules.AddRange(rules);
        var rewrites = new GitUrlRewriteService(
            new InMemoryAppConfigStore { Config = config },
            git,
            new NoOpBackupService());

        Assert.True(GitServiceValidator.Validate(github, config).IsValid);
        Assert.True(OwnerRouteValidator.Validate(
            serviceRoute,
            config,
            null,
            null,
            GitProviderAdapterRegistry.CreateDefault()).IsValid);
        Assert.Equal("git@github-default:other/public.git",
            (await rewrites.PreviewAsync("https://github.com/other/public.git")).RewrittenUrl);
        Assert.Equal("git@github-owner:special-org/server.git",
            (await rewrites.PreviewAsync("https://github.com/special-org/server.git")).RewrittenUrl);
        Assert.Equal("git@github-repository:special-org/private-repo.git",
            (await rewrites.PreviewAsync("https://github.com/special-org/private-repo.git")).RewrittenUrl);
    }

    [Fact]
    public async Task Reconcile_IsIdempotentAndPreservesUnrelatedValuesUnderManagedKey()
    {
        var store = new FakeGitUrlRewriteStore();
        var desired = new GitUrlRewriteRule("git@gitea-cloud:", "https://git.policoil.top/");
        var unrelated = new GitUrlRewriteRule("git@gitea-cloud:", "https://manual.example/");
        store.Rules.Add(desired);
        store.Rules.Add(desired);
        store.Rules.Add(unrelated);
        var backup = new NoOpBackupService();
        var service = new GitUrlRewriteService(new InMemoryAppConfigStore(), store, backup);
        var plan = new GitRewritePlan();
        plan.Removes.Add(desired);
        plan.Adds.Add(desired);
        plan.CaptureOriginalValues(
            desired.ConfigKey,
            [desired.InsteadOfUrl, desired.InsteadOfUrl, unrelated.InsteadOfUrl]);

        var first = await service.ApplyPlanAsync(plan, "deduplicate");
        var second = await service.ApplyPlanAsync(
            await service.BuildCleanupDuplicatesPlanAsync(),
            "deduplicate again");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Single(store.Rules, rule => rule == desired);
        Assert.Contains(unrelated, store.Rules);
        Assert.Equal(1, backup.SnapshotCount);
        Assert.Empty(second.Value ?? []);
    }

    [Fact]
    public async Task LegacyAccountOwnerMigration_IsPreviewOnlyUntilApplied()
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var config = new AppConfig
        {
            GitServices = [GitServiceInstance.CreateGitHubCom(), service],
            Identities = [identity]
        };
        var store = new FakeGitUrlRewriteStore();
        store.Rules.AddRange(GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.Gitea)
            .BuildRewriteRules(service, identity, Route(service, identity, GitRouteScope.Owner, identity.AccountName)));
        var rewrites = new GitUrlRewriteService(new InMemoryAppConfigStore { Config = config }, store, new NoOpBackupService());

        var plan = await rewrites.BuildLegacyAccountOwnerMigrationPlanAsync();

        Assert.Equal(4, plan.Removes.Count);
        Assert.Equal(4, plan.Adds.Count);
        Assert.Contains(store.Rules, rule => rule.InsteadOfUrl.EndsWith("/fgc0109/", StringComparison.Ordinal));
        Assert.True((await rewrites.ApplyPlanAsync(plan, "confirm migration")).Success);
        Assert.DoesNotContain(store.Rules, rule => rule.InsteadOfUrl.EndsWith("/fgc0109/", StringComparison.Ordinal));
        Assert.Contains(store.Rules, rule => rule.InsteadOfUrl == "git@git.policoil.top:");
        Assert.Contains(store.Rules, rule => rule.InsteadOfUrl == "https://git.policoil.top/");
    }

    [Theory]
    [InlineData("git@git.policoil.top:project-base/proto-tool-pb-extra.git", "git@git.policoil.top:")]
    [InlineData("https://git.policoil.top/project-base/proto-tool-pb-extra.git", "https://git.policoil.top/")]
    [InlineData("ssh://git@git.policoil.top/project-base/proto-tool-pb-extra.git", "ssh://git@git.policoil.top/")]
    [InlineData("git+ssh://git@git.policoil.top/project-base/proto-tool-pb-extra.git", "git+ssh://git@git.policoil.top/")]
    public async Task CloudPreview_RewritesEverySupportedUrlFormat(string originalUrl, string expectedPrefix)
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), service],
                Identities = [identity]
            }
        };
        var store = new FakeGitUrlRewriteStore();
        configStore.Config.Normalize();
        store.Rules.AddRange(OwnerRouteService.BuildExpectedRules(configStore.Config));
        var rewrites = new GitUrlRewriteService(configStore, store, new NoOpBackupService());

        var preview = await rewrites.PreviewAsync(originalUrl);

        Assert.Equal(expectedPrefix, preview.ExpectedMatchedPrefix);
        Assert.Equal("git@gitea-cloud:", preview.ExpectedMatchedBaseUrl);
        Assert.Equal("git@gitea-cloud:project-base/proto-tool-pb-extra.git", preview.ExpectedRewrittenUrl);
        Assert.Equal(UrlRewriteExpectationStatus.Applied, preview.ExpectationStatus);
    }

    [Theory]
    [InlineData("project-base", "proto-tool-pb-extra.git")]
    [InlineData("game-riki", "server.git")]
    [InlineData("game-hhmx", "client.git")]
    public async Task ServiceRoute_CoversEveryOrganization(string owner, string repository)
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), service],
                Identities = [identity]
            }
        };
        configStore.Config.Normalize();
        var store = new FakeGitUrlRewriteStore();
        store.Rules.AddRange(OwnerRouteService.BuildExpectedRules(configStore.Config));
        var rewrites = new GitUrlRewriteService(configStore, store, new NoOpBackupService());

        var preview = await rewrites.PreviewAsync($"git@git.policoil.top:{owner}/{repository}");

        Assert.Equal($"git@gitea-cloud:{owner}/{repository}", preview.RewrittenUrl);
        Assert.DoesNotContain("fgc0109/", preview.MatchedBaseUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_ShowsExpectedRouteWhenGlobalGitConfigIsMissing()
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), service],
                Identities = [identity]
            }
        };
        var rewrites = new GitUrlRewriteService(configStore, new FakeGitUrlRewriteStore(), new NoOpBackupService());

        var preview = await rewrites.PreviewAsync("git@git.policoil.top:project-base/proto-tool-pb-extra.git");

        Assert.Null(preview.ActualMatchedPrefix);
        Assert.Equal("git@git.policoil.top:", preview.ExpectedMatchedPrefix);
        Assert.Equal("git@gitea-cloud:", preview.ExpectedMatchedBaseUrl);
        Assert.Equal("git@gitea-cloud:project-base/proto-tool-pb-extra.git", preview.ExpectedRewrittenUrl);
        Assert.Equal(UrlRewriteExpectationStatus.Missing, preview.ExpectationStatus);
    }

    [Fact]
    public void Normalize_DerivesServiceRouteWithoutTreatingAccountNameAsOwner()
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var config = new AppConfig
        {
            GitServices = [GitServiceInstance.CreateGitHubCom(), service],
            Identities = [identity]
        };

        config.Normalize();

        var route = Assert.Single(config.RepositoryRoutes, item => item.ServiceInstanceId == service.Id);
        Assert.Equal(GitRouteScope.Service, route.Scope);
        Assert.Equal(string.Empty, route.NamespacePath);
        Assert.DoesNotContain(config.RepositoryRoutes, item => item.Scope == GitRouteScope.Owner);
    }

    [Fact]
    public async Task LegacyMigration_RemovesOnlyGiteaAccountRouteAndKeepsGitHubOwnerRoute()
    {
        var gitea = GitServiceService.CreateTemplate("Gitea Cloud");
        var giteaIdentity = Identity("gitea-cloud", gitea.Id, "gitea-cloud");
        gitea.DefaultIdentityId = giteaIdentity.Id;
        var github = GitServiceInstance.CreateGitHubCom();
        var githubIdentity = Identity("github-fgc", github.Id, "fgc0109");
        var legacyRoute = Route(gitea, giteaIdentity, GitRouteScope.Owner, "fgc0109");
        legacyRoute.Id = "legacy-gitea-owner";
        var githubRoute = Route(github, githubIdentity, GitRouteScope.Owner, "fgc0109");
        githubRoute.Id = "github-owner";
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [github, gitea],
                Identities = [githubIdentity, giteaIdentity],
                RepositoryRoutes = [legacyRoute, githubRoute]
            }
        };
        var store = new FakeGitUrlRewriteStore();
        store.Rules.AddRange(GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.Gitea)
            .BuildRewriteRules(gitea, giteaIdentity, legacyRoute));
        store.Rules.AddRange(GitProviderAdapterRegistry.CreateDefault().Get(GitProviderKind.GitHub)
            .BuildRewriteRules(github, githubIdentity, githubRoute));
        var rewrites = new GitUrlRewriteService(configStore, store, new NoOpBackupService());

        var plan = await rewrites.BuildLegacyAccountOwnerMigrationPlanAsync();

        Assert.Contains("legacy-gitea-owner", plan.RepositoryRouteIdsToRemove);
        Assert.DoesNotContain("github-owner", plan.RepositoryRouteIdsToRemove);
        Assert.DoesNotContain(plan.Removes, rule => rule.InsteadOfUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase));
        Assert.True((await rewrites.ApplyPlanAsync(plan, "migrate legacy Gitea route")).Success);
        Assert.Contains(configStore.Config.RepositoryRoutes, route => route.Id == "github-owner");
        Assert.DoesNotContain(configStore.Config.RepositoryRoutes, route => route.Id == "legacy-gitea-owner");
        Assert.Contains(configStore.Config.RepositoryRoutes, route => route.ServiceInstanceId == gitea.Id && route.Scope == GitRouteScope.Service);
        Assert.Contains(store.Rules, rule => rule.BaseUrl == "git@fgc0109:fgc0109/" && rule.InsteadOfUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ServiceRepairPlan_DoesNotCrossLocalCloudOrGitHubSources()
    {
        var cloud = GitServiceService.CreateTemplate("Gitea Cloud");
        var local = GitServiceService.CreateTemplate("Gitea Local");
        var cloudIdentity = Identity("gitea-cloud", cloud.Id, "gitea-cloud");
        var localIdentity = Identity("gitea-local", local.Id, "gitea-local");
        cloud.DefaultIdentityId = cloudIdentity.Id;
        local.DefaultIdentityId = localIdentity.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), cloud, local],
                Identities = [cloudIdentity, localIdentity]
            }
        };
        var store = new FakeGitUrlRewriteStore();
        store.Rules.Add(new GitUrlRewriteRule("git@gitea-local:", "git@git.policoil.top:"));
        store.Rules.Add(new GitUrlRewriteRule("git@fgc0109:fgc0109/", "https://github.com/fgc0109/"));
        var rewrites = new GitUrlRewriteService(configStore, store, new NoOpBackupService());

        var plan = await rewrites.BuildServiceRepairPlanAsync(cloud.Id);

        Assert.Contains(plan.Removes, rule => rule.BaseUrl == "git@gitea-local:" && rule.InsteadOfUrl == "git@git.policoil.top:");
        Assert.Contains(plan.Adds, rule => rule.BaseUrl == "git@gitea-cloud:" && rule.InsteadOfUrl == "git@git.policoil.top:");
        Assert.DoesNotContain(plan.Adds.Concat(plan.Removes), rule => rule.InsteadOfUrl.Contains("gitea.lan.policoil.top", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Adds.Concat(plan.Removes), rule => rule.InsteadOfUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoteTest_AcceptsAuthenticatedNonZeroExitCodeWithoutPasswordFallback()
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), service],
                Identities = [identity]
            }
        };
        var store = new FakeGitUrlRewriteStore
        {
            RemoteResult = new ProcessResult
            {
                ExecutablePath = "git.exe",
                Arguments = ["ls-remote"],
                ExitCode = 1,
                StandardError = "Authenticated to git.policoil.top using publickey. Gitea: successfully authenticated"
            }
        };
        var rewrites = new GitUrlRewriteService(configStore, store, new NoOpBackupService());

        var result = await rewrites.TestRemoteRouteAsync("git@git.policoil.top:project-base/proto-tool-pb-extra.git");

        Assert.True(result.AuthenticationSucceeded);
        Assert.False(result.PasswordFallbackDetected);
        Assert.Equal(service.Id, result.Service?.Id);
        Assert.Contains("non-zero", result.Classification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteTest_RejectsPasswordFallbackEvenWhenEndpointWasReached()
    {
        var service = GitServiceService.CreateTemplate("Gitea Cloud");
        var identity = Identity("gitea-cloud", service.Id, "gitea-cloud");
        service.DefaultIdentityId = identity.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), service],
                Identities = [identity]
            }
        };
        var store = new FakeGitUrlRewriteStore
        {
            RemoteResult = new ProcessResult
            {
                ExecutablePath = "git.exe",
                Arguments = ["ls-remote"],
                ExitCode = 1,
                StandardError = "git@git.policoil.top's password:"
            }
        };
        var rewrites = new GitUrlRewriteService(configStore, store, new NoOpBackupService());

        var result = await rewrites.TestRemoteRouteAsync("git@git.policoil.top:project-base/proto-tool-pb-extra.git");

        Assert.False(result.AuthenticationSucceeded);
        Assert.True(result.PasswordFallbackDetected);
        Assert.Contains("password", result.Classification, StringComparison.OrdinalIgnoreCase);
    }

    private static GitIdentity Identity(string id, string serviceId, string alias, string account = "fgc0109")
        => new()
        {
            Id = id,
            ServiceInstanceId = serviceId,
            DisplayName = id,
            AccountName = account,
            HostAlias = alias,
            PrivateKeyPath = SharedPrivateKey,
            PublicKeyPath = SharedPublicKey
        };

    private static RepositoryRoute Route(GitServiceInstance service, GitIdentity identity, GitRouteScope scope, string? owner = null, string? repository = null)
    {
        var route = new RepositoryRoute
        {
            ServiceInstanceId = service.Id,
            IdentityId = identity.Id,
            Scope = scope,
            Owner = owner,
            Repository = repository
        };
        route.Normalize();
        return route;
    }

    private static GitServiceService CreateServiceManager(InMemoryAppConfigStore store)
        => new(
            store,
            new NoOpBackupService(),
            new StubProcessRunner(_ => new ProcessResult { ExecutablePath = "ssh.exe" }),
            new FixedToolchainService("git.exe", sshPath: "ssh.exe"),
            GitProviderAdapterRegistry.CreateDefault());
}
