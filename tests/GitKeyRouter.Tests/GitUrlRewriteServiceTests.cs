using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class GitUrlRewriteServiceTests
{
    [Fact]
    public async Task Compare_DetectsDuplicateAndConflict()
    {
        var configStore = ConfigStore();
        var git = new FakeGitUrlRewriteStore();
        git.Rules.Add(new GitUrlRewriteRule("git@github-camus:camus0109/", "https://github.com/camus0109/"));
        git.Rules.Add(new GitUrlRewriteRule("git@github-camus:camus0109/", "https://github.com/camus0109/"));
        git.Rules.Add(new GitUrlRewriteRule("git@wrong:camus0109/", "git@github.com:camus0109/"));
        var service = new GitUrlRewriteService(configStore, git, new NoOpBackupService());

        var comparison = await service.CompareAsync();

        Assert.Contains(comparison, item => item.InsteadOfUrl.StartsWith("https://", StringComparison.Ordinal) && item.Status == GitRewriteStatus.Duplicate);
        Assert.Contains(comparison, item => item.InsteadOfUrl.StartsWith("git@", StringComparison.Ordinal) && item.Status == GitRewriteStatus.Conflict);
    }

    [Fact]
    public async Task Preview_UsesLongestMatchingPrefix()
    {
        var configStore = ConfigStore();
        var git = new FakeGitUrlRewriteStore();
        git.Rules.Add(new GitUrlRewriteRule("git@generic:", "https://github.com/"));
        git.Rules.Add(new GitUrlRewriteRule("git@github-camus:camus0109/", "https://github.com/camus0109/"));
        var service = new GitUrlRewriteService(configStore, git, new NoOpBackupService());

        var preview = await service.PreviewAsync("https://github.com/camus0109/panel-terraria.git");

        Assert.Equal("git@github-camus:camus0109/panel-terraria.git", preview.RewrittenUrl);
    }

    [Fact]
    public async Task CleanupDuplicates_RemovesAllThenAddsOne()
    {
        var configStore = ConfigStore();
        var git = new FakeGitUrlRewriteStore();
        var rule = new GitUrlRewriteRule("git@github-camus:camus0109/", "https://github.com/camus0109/");
        git.Rules.Add(rule);
        git.Rules.Add(rule);
        var service = new GitUrlRewriteService(configStore, git, new NoOpBackupService());

        var plan = await service.BuildCleanupDuplicatesPlanAsync();
        var result = await service.ApplyPlanAsync(plan, "test");

        Assert.True(result.Success);
        Assert.Single(git.Rules, item => item == rule);
    }

    [Fact]
    public async Task ApplyPlan_RestoresAllAffectedKeysAfterPartialFailure()
    {
        const string baseUrl = "git@managed:";
        var oldOne = new GitUrlRewriteRule(baseUrl, "https://old-one.example/");
        var oldTwo = new GitUrlRewriteRule(baseUrl, "https://old-two.example/");
        var newOne = new GitUrlRewriteRule(baseUrl, "https://new-one.example/");
        var newTwo = new GitUrlRewriteRule(baseUrl, "https://new-two.example/");
        var unrelated = new GitUrlRewriteRule("git@unrelated:", "https://unrelated.example/");
        var git = new FakeGitUrlRewriteStore();
        git.Rules.AddRange([oldOne, oldTwo, unrelated]);
        git.FailAddAttempts.Add(2);
        var backup = new NoOpBackupService();
        var service = new GitUrlRewriteService(ConfigStore(), git, backup);
        var plan = new GitRewritePlan();
        plan.Removes.AddRange([oldOne, oldTwo]);
        plan.Adds.AddRange([newOne, newTwo]);
        plan.CaptureOriginalValues(oldOne.ConfigKey, [oldOne.InsteadOfUrl, oldTwo.InsteadOfUrl]);

        var result = await service.ApplyPlanAsync(plan, "test rollback");

        Assert.False(result.Success);
        Assert.Contains("restored automatically", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [oldOne.InsteadOfUrl, oldTwo.InsteadOfUrl],
            await git.GetValuesAsync(oldOne.ConfigKey));
        Assert.Contains(unrelated, git.Rules);
        Assert.Equal(1, backup.SnapshotCount);
    }

    [Fact]
    public async Task ApplyPlan_ReportsApplyAndRollbackFailuresSeparately()
    {
        const string baseUrl = "git@managed:";
        var oldOne = new GitUrlRewriteRule(baseUrl, "https://old-one.example/");
        var oldTwo = new GitUrlRewriteRule(baseUrl, "https://old-two.example/");
        var newOne = new GitUrlRewriteRule(baseUrl, "https://new-one.example/");
        var newTwo = new GitUrlRewriteRule(baseUrl, "https://new-two.example/");
        var git = new FakeGitUrlRewriteStore();
        git.Rules.AddRange([oldOne, oldTwo]);
        git.FailAddAttempts.UnionWith([2, 3]);
        var service = new GitUrlRewriteService(ConfigStore(), git, new NoOpBackupService());
        var plan = new GitRewritePlan();
        plan.Removes.AddRange([oldOne, oldTwo]);
        plan.Adds.AddRange([newOne, newTwo]);
        plan.CaptureOriginalValues(oldOne.ConfigKey, [oldOne.InsteadOfUrl, oldTwo.InsteadOfUrl]);

        var result = await service.ApplyPlanAsync(plan, "test rollback failure");

        Assert.False(result.Success);
        Assert.Contains("rollback also failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Errors, error => error.StartsWith("Apply:", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.StartsWith("Rollback:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyPlan_VerifiesExactTargetWithoutTouchingUnrelatedKeys()
    {
        const string baseUrl = "git@managed:";
        var oldOne = new GitUrlRewriteRule(baseUrl, "https://old-one.example/");
        var oldTwo = new GitUrlRewriteRule(baseUrl, "https://old-two.example/");
        var newOne = new GitUrlRewriteRule(baseUrl, "https://new-one.example/");
        var newTwo = new GitUrlRewriteRule(baseUrl, "https://new-two.example/");
        var unrelated = new GitUrlRewriteRule("git@unrelated:", "https://unrelated.example/");
        var git = new FakeGitUrlRewriteStore();
        git.Rules.AddRange([oldOne, oldTwo, unrelated]);
        var service = new GitUrlRewriteService(ConfigStore(), git, new NoOpBackupService());
        var plan = new GitRewritePlan();
        plan.Removes.AddRange([oldOne, oldTwo]);
        plan.Adds.AddRange([newOne, newTwo]);
        plan.CaptureOriginalValues(oldOne.ConfigKey, [oldOne.InsteadOfUrl, oldTwo.InsteadOfUrl]);

        var result = await service.ApplyPlanAsync(plan, "test exact target");

        Assert.True(result.Success);
        Assert.Equal(
            [newOne.InsteadOfUrl, newTwo.InsteadOfUrl],
            await git.GetValuesAsync(newOne.ConfigKey));
        Assert.Contains(unrelated, git.Rules);
    }

    [Fact]
    public async Task ApplyPlan_RejectsAffectedKeyOrderChangeAfterPreview()
    {
        const string baseUrl = "git@managed:";
        var oldOne = new GitUrlRewriteRule(baseUrl, "https://old-one.example/");
        var oldTwo = new GitUrlRewriteRule(baseUrl, "https://old-two.example/");
        var replacement = new GitUrlRewriteRule(baseUrl, "https://new.example/");
        var git = new FakeGitUrlRewriteStore();
        git.Rules.AddRange([oldOne, oldTwo]);
        var backup = new NoOpBackupService();
        var service = new GitUrlRewriteService(ConfigStore(), git, backup);
        var plan = new GitRewritePlan();
        plan.Removes.Add(oldOne);
        plan.Adds.Add(replacement);
        plan.CaptureOriginalValues(oldOne.ConfigKey, [oldOne.InsteadOfUrl, oldTwo.InsteadOfUrl]);
        git.Rules.Clear();
        git.Rules.AddRange([oldTwo, oldOne]);

        var result = await service.ApplyPlanAsync(plan, "stale order");

        Assert.False(result.Success);
        Assert.Contains("changed after the preview", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([oldTwo, oldOne], git.Rules);
        Assert.Equal(0, backup.SnapshotCount);
    }

    [Fact]
    public async Task ApplyPlan_AllowsChangesToPlanExternalKeys()
    {
        const string baseUrl = "git@managed:";
        var oldRule = new GitUrlRewriteRule(baseUrl, "https://old.example/");
        var replacement = new GitUrlRewriteRule(baseUrl, "https://new.example/");
        var unrelated = new GitUrlRewriteRule("git@unrelated:", "https://external.example/");
        var git = new FakeGitUrlRewriteStore();
        git.Rules.Add(oldRule);
        var service = new GitUrlRewriteService(ConfigStore(), git, new NoOpBackupService());
        var plan = new GitRewritePlan();
        plan.Removes.Add(oldRule);
        plan.Adds.Add(replacement);
        plan.CaptureOriginalValues(oldRule.ConfigKey, [oldRule.InsteadOfUrl]);
        git.Rules.Add(unrelated);

        var result = await service.ApplyPlanAsync(plan, "external key change");

        Assert.True(result.Success);
        Assert.Contains(replacement, git.Rules);
        Assert.Contains(unrelated, git.Rules);
    }

    [Fact]
    public async Task ApplyPlan_RejectsStaleConfigurationBeforeRouteRemoval()
    {
        var configStore = ConfigStore();
        var snapshot = await configStore.LoadSnapshotAsync();
        var plan = new GitRewritePlan { ConfigVersion = snapshot.Version };
        plan.RepositoryRouteIdsToRemove.Add("route-to-remove");
        configStore.SimulateExternalChange(new AppConfig { UiLanguage = "en-US" });
        var backup = new NoOpBackupService();
        var service = new GitUrlRewriteService(configStore, new FakeGitUrlRewriteStore(), backup);

        var result = await service.ApplyPlanAsync(plan, "stale config");

        Assert.False(result.Success);
        Assert.Contains("configuration changed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backup.SnapshotCount);
        Assert.Equal("en-US", configStore.Config.UiLanguage);
    }

    [Fact]
    public async Task Compare_MapsRulesToTheirServiceAndNamespace()
    {
        var gitLab = new GitServiceInstance
        {
            Id = "gitlab-office",
            DisplayName = "Office GitLab",
            ProviderKind = GitProviderKind.GitLab,
            HostName = "gitlab.office.example",
            SshUser = "git",
            WebBaseUrl = "https://gitlab.office.example"
        };
        var identity = new GitIdentity
        {
            Id = "work",
            ServiceInstanceId = gitLab.Id,
            DisplayName = "Work",
            AccountName = "camus",
            HostAlias = "gitlab-work"
        };
        var store = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), gitLab],
                Identities = [identity],
                RepositoryRoutes =
                [
                    new RepositoryRoute
                    {
                        ServiceInstanceId = gitLab.Id,
                        NamespacePath = "company/platform",
                        IdentityId = identity.Id,
                        Enabled = true
                    }
                ]
            }
        };
        var git = new FakeGitUrlRewriteStore();
        var service = new GitUrlRewriteService(store, git, new NoOpBackupService());

        var comparisons = await service.CompareAsync();

        Assert.All(comparisons.Where(item => item.Status == GitRewriteStatus.Missing), item =>
        {
            Assert.Equal(gitLab.Id, item.ServiceInstanceId);
            Assert.Equal("company/platform", item.NamespacePath);
            Assert.Null(item.GitHubOwner);
        });
    }

    [Fact]
    public async Task DeleteRoutePlan_OnlyRemovesRulesForSelectedServiceNamespace()
    {
        var gitLab = new GitServiceInstance
        {
            Id = "gitlab-office",
            DisplayName = "Office GitLab",
            ProviderKind = GitProviderKind.GitLab,
            HostName = "gitlab.office.example",
            SshUser = "git",
            WebBaseUrl = "https://gitlab.office.example"
        };
        var githubIdentity = new GitIdentity
        {
            Id = "github",
            ServiceInstanceId = GitServiceInstance.GitHubComId,
            DisplayName = "GitHub",
            HostAlias = "github-team"
        };
        var gitLabIdentity = new GitIdentity
        {
            Id = "gitlab",
            ServiceInstanceId = gitLab.Id,
            DisplayName = "GitLab",
            HostAlias = "gitlab-team"
        };
        var config = new AppConfig
        {
            GitServices = [GitServiceInstance.CreateGitHubCom(), gitLab],
            Identities = [githubIdentity, gitLabIdentity],
            RepositoryRoutes =
            [
                new RepositoryRoute
                {
                    ServiceInstanceId = GitServiceInstance.GitHubComId,
                    NamespacePath = "team",
                    IdentityId = githubIdentity.Id,
                    Enabled = true
                },
                new RepositoryRoute
                {
                    ServiceInstanceId = gitLab.Id,
                    NamespacePath = "team",
                    IdentityId = gitLabIdentity.Id,
                    Enabled = true
                }
            ]
        };
        var expected = OwnerRouteService.BuildExpectedRules(config);
        var git = new FakeGitUrlRewriteStore();
        git.Rules.AddRange(expected);
        var service = new GitUrlRewriteService(
            new InMemoryAppConfigStore { Config = config },
            git,
            new NoOpBackupService());

        var plan = await service.BuildDeleteRoutePlanAsync(gitLab.Id, "team");

        Assert.Equal(4, plan.Removes.Count);
        Assert.All(plan.Removes, rule => Assert.Contains("gitlab.office.example", rule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Removes, rule => rule.InsteadOfUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase));
    }

    private static InMemoryAppConfigStore ConfigStore()
        => new()
        {
            Config = new AppConfig
            {
                Identities =
                [
                    new GitIdentity
                    {
                        Id = "camus",
                        ServiceInstanceId = GitServiceInstance.GitHubComId,
                        DisplayName = "Camus",
                        HostAlias = "github-camus"
                    }
                ],
                RepositoryRoutes =
                [
                    new RepositoryRoute
                    {
                        ServiceInstanceId = GitServiceInstance.GitHubComId,
                        Scope = GitRouteScope.Owner,
                        Owner = "camus0109",
                        IdentityId = "camus",
                        Enabled = true
                    }
                ]
            }
        };
}
