using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class SshConfigServiceTests
{
    [Fact]
    public void Upsert_AddsManagedBlock()
    {
        var service = CreateService();
        var preview = service.PreviewUpsert("Host internal\r\n    HostName internal.example\r\n", Identity("github-camus", @"C:\Users\fgc01\.ssh\id_ed25519_camus"));

        Assert.Contains("# BEGIN GitKeyRouter managed block: github-camus", preview.UpdatedText);
        Assert.Contains("IdentityFile C:/Users/fgc01/.ssh/id_ed25519_camus", preview.UpdatedText);
        Assert.Contains("Host internal", preview.UpdatedText);
    }

    [Fact]
    public void Upsert_UpdatesOnlyMatchingManagedBlock()
    {
        var service = CreateService();
        var original = service.PreviewUpsert(string.Empty, Identity("github-camus", @"C:\old-key")).UpdatedText
            + "# user comment\r\n";

        var preview = service.PreviewUpsert(original, Identity("github-camus", @"D:\keys\new-key"));

        Assert.DoesNotContain("C:/old-key", preview.UpdatedText);
        Assert.Contains("D:/keys/new-key", preview.UpdatedText);
        Assert.Contains("# user comment", preview.UpdatedText);
        Assert.Single(service.ParseManagedBlocks(preview.UpdatedText));
    }

    [Fact]
    public void Delete_RemovesOnlyManagedBlock()
    {
        var service = CreateService();
        var original = "# keep this\r\nHost internal\r\n    HostName example.test\r\n\r\n"
            + service.PreviewUpsert(string.Empty, Identity("github-camus", @"C:\key")).UpdatedText;

        var preview = service.PreviewDelete(original, "github-camus");

        Assert.DoesNotContain("GitKeyRouter managed block", preview.UpdatedText);
        Assert.Contains("# keep this", preview.UpdatedText);
        Assert.Contains("Host internal", preview.UpdatedText);
    }

    [Fact]
    public void Upsert_PreservesCommentsAndCrLf()
    {
        var service = CreateService();
        var original = "# first comment\r\nHost old\r\n    HostName old.example\r\n";

        var preview = service.PreviewUpsert(original, Identity("github-project-base", @"C:\Users\x\.ssh\id_ed25519_project_base"));

        Assert.StartsWith(original, preview.UpdatedText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", preview.UpdatedText.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.Contains("\r\n", preview.UpdatedText);
    }

    [Fact]
    public void ParseManagedBlocks_DetectsDuplicates()
    {
        var service = CreateService();
        var block = service.PreviewUpsert(string.Empty, Identity("github-camus", @"C:\key")).UpdatedText;
        var duplicate = block + block;

        Assert.Equal(2, service.ParseManagedBlocks(duplicate).Count);
        Assert.Throws<InvalidOperationException>(() => service.PreviewUpsert(duplicate, Identity("github-camus", @"D:\key")));
    }

    [Fact]
    public void ParseUnmanagedHostAliases_IgnoresManagedBlocksWildcardsAndNegations()
    {
        var service = CreateService();
        var managed = service.PreviewUpsert(string.Empty, Identity("github-camus", @"C:\key")).UpdatedText;
        var config = "Host work-git backup-git\n    HostName git.example\n"
            + "Host * !blocked wildcard-*\n    ServerAliveInterval 30\n"
            + managed;

        var aliases = service.ParseUnmanagedHostAliases(config);

        Assert.Equal(["backup-git", "work-git"], aliases);
        Assert.DoesNotContain("github-camus", aliases);
    }

    [Fact]
    public async Task Apply_WritesWhenPreviewSnapshotStillMatches()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.SshDirectory);
        const string original = "Host internal\n    HostName internal.example\n";
        await File.WriteAllTextAsync(paths.SshConfigPath, original);
        var service = new SshConfigService(new PhysicalFileSystem(), paths, new NoOpBackupService());
        var preview = service.PreviewUpsert(original, Identity("github-camus", @"C:\key"));

        var result = await service.ApplyAsync(preview, "test unchanged preview");

        Assert.True(result.Success);
        Assert.Equal(preview.UpdatedText, await File.ReadAllTextAsync(paths.SshConfigPath));
    }

    [Fact]
    public async Task Apply_RejectsExistingFileChangedAfterPreview()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(paths.SshDirectory);
        const string original = "Host internal\n    HostName internal.example\n";
        await File.WriteAllTextAsync(paths.SshConfigPath, original);
        var service = new SshConfigService(new PhysicalFileSystem(), paths, new NoOpBackupService());
        var preview = service.PreviewUpsert(original, Identity("github-camus", @"C:\key"));
        const string externalEdit = "# changed by another editor\n";
        await File.WriteAllTextAsync(paths.SshConfigPath, externalEdit);

        var result = await service.ApplyAsync(preview, "test stale preview");

        Assert.False(result.Success);
        Assert.Contains("预览后", result.Message, StringComparison.Ordinal);
        Assert.Equal(externalEdit, await File.ReadAllTextAsync(paths.SshConfigPath));
    }

    [Fact]
    public async Task Apply_RejectsFileCreatedAfterPreview()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var service = new SshConfigService(new PhysicalFileSystem(), paths, new NoOpBackupService());
        var preview = service.PreviewUpsert(string.Empty, Identity("github-camus", @"C:\key"));
        Directory.CreateDirectory(paths.SshDirectory);
        const string externalEdit = "# created by another editor\n";
        await File.WriteAllTextAsync(paths.SshConfigPath, externalEdit);

        var result = await service.ApplyAsync(preview, "test stale preview");

        Assert.False(result.Success);
        Assert.Contains("预览后", result.Message, StringComparison.Ordinal);
        Assert.Equal(externalEdit, await File.ReadAllTextAsync(paths.SshConfigPath));
    }

    private static SshConfigService CreateService()
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitKeyRouter.Tests", Guid.NewGuid().ToString("N"));
        return new SshConfigService(new PhysicalFileSystem(), new TestAppPaths(temp), new NoOpBackupService());
    }

    private static GitIdentity Identity(string alias, string privateKey)
        => new()
        {
            DisplayName = alias,
            ServiceInstanceId = GitServiceInstance.GitHubComId,
            AccountName = "owner",
            HostAlias = alias,
            PrivateKeyPath = privateKey,
            PublicKeyPath = privateKey + ".pub"
        };
}
