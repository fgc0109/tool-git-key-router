using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.Git;
using GitKeyRouter.Infrastructure.ProcessExecution;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class GitUrlRewriteStoreIntegrationTests
{
    [Fact]
    public async Task UsesIsolatedGlobalConfigFile()
    {
        var gitPath = FindGit();
        if (gitPath is null)
        {
            return;
        }

        using var temp = new TemporaryDirectory();
        var isolatedConfig = Path.Combine(temp.Path, "global.gitconfig");
        var store = new GitUrlRewriteStore(
            new ProcessRunner(),
            new FixedToolchainService(gitPath),
            new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = isolatedConfig });
        var rule = new GitUrlRewriteRule("git@github-test:owner/", "https://github.com/owner/");

        Assert.True((await store.AddAsync(rule)).Succeeded);
        Assert.Contains(rule, await store.GetAllAsync());
        Assert.True((await store.RemoveAllAsync(rule)).Succeeded);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task ReconcileUsesGetAllAndUnsetAllInIsolatedGlobalConfig()
    {
        var gitPath = FindGit();
        if (gitPath is null)
        {
            return;
        }

        using var temp = new TemporaryDirectory();
        var isolatedConfig = Path.Combine(temp.Path, "global.gitconfig");
        var store = new GitUrlRewriteStore(
            new ProcessRunner(),
            new FixedToolchainService(gitPath),
            new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = isolatedConfig });
        var desired = new GitUrlRewriteRule("git@gitea-cloud:", "https://git.policoil.top/");
        var unrelated = new GitUrlRewriteRule("git@gitea-cloud:", "https://manual.example/");
        Assert.True((await store.AddAsync(desired)).Succeeded);
        Assert.True((await store.AddAsync(desired)).Succeeded);
        Assert.True((await store.AddAsync(unrelated)).Succeeded);
        var backup = new NoOpBackupService();
        var service = new GitKeyRouter.Core.Services.GitUrlRewriteService(new InMemoryAppConfigStore(), store, backup);
        var plan = new GitRewritePlan();
        plan.Removes.Add(desired);
        plan.Adds.Add(desired);
        plan.CaptureOriginalValues(
            desired.ConfigKey,
            [desired.InsteadOfUrl, desired.InsteadOfUrl, unrelated.InsteadOfUrl]);

        Assert.True((await service.ApplyPlanAsync(plan, "isolated reconcile")).Success);
        Assert.True((await service.ApplyPlanAsync(
            await service.BuildCleanupDuplicatesPlanAsync(),
            "isolated reconcile again")).Success);

        var values = await store.GetValuesAsync(desired.ConfigKey);
        Assert.Equal(2, values.Count);
        Assert.Single(values, value => value == desired.InsteadOfUrl);
        Assert.Contains(unrelated.InsteadOfUrl, values);
        Assert.Equal(1, backup.SnapshotCount);
        Assert.True(File.Exists(isolatedConfig));
    }

    private static string? FindGit()
    {
        var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
