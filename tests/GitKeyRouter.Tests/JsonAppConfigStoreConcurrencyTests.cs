using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.Configuration;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class JsonAppConfigStoreConcurrencyTests
{
    [Fact]
    public async Task SaveIfUnchanged_RejectsFileCreatedAfterMissingSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var store = new JsonAppConfigStore(paths, new PhysicalFileSystem());
        var snapshot = await store.LoadSnapshotAsync();
        Directory.CreateDirectory(paths.AppDataDirectory);
        await File.WriteAllTextAsync(paths.ConfigPath, "{\"SchemaVersion\":4,\"UiLanguage\":\"external\"}");

        await Assert.ThrowsAsync<AppConfigConcurrencyException>(() =>
            store.SaveIfUnchangedAsync(new AppConfig { UiLanguage = "ours" }, snapshot.Version));

        Assert.Contains("external", await File.ReadAllTextAsync(paths.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveIfUnchanged_RejectsFileReplacementAndPreservesExternalBytes()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var store = new JsonAppConfigStore(paths, new PhysicalFileSystem());
        await store.SaveAsync(new AppConfig { UiLanguage = "before" });
        var snapshot = await store.LoadSnapshotAsync();
        const string external = "{\"SchemaVersion\":4,\"UiLanguage\":\"external replacement\"}";
        await File.WriteAllTextAsync(paths.ConfigPath, external);

        await Assert.ThrowsAsync<AppConfigConcurrencyException>(() =>
            store.SaveIfUnchangedAsync(new AppConfig { UiLanguage = "ours" }, snapshot.Version));

        Assert.Equal(external, await File.ReadAllTextAsync(paths.ConfigPath));
    }

    [Fact]
    public async Task SaveIfUnchanged_ReturnsNewVersionAndRejectsReuseOfOldVersion()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var store = new JsonAppConfigStore(paths, new PhysicalFileSystem());
        var snapshot = await store.LoadSnapshotAsync();

        var savedVersion = await store.SaveIfUnchangedAsync(
            new AppConfig { UiLanguage = "zh-CN" },
            snapshot.Version);

        Assert.True(savedVersion.Exists);
        Assert.NotEmpty(savedVersion.Sha256);
        await Assert.ThrowsAsync<AppConfigConcurrencyException>(() =>
            store.SaveIfUnchangedAsync(new AppConfig { UiLanguage = "en-US" }, snapshot.Version));
        Assert.Equal("zh-CN", (await store.LoadAsync()).UiLanguage);
    }
}
