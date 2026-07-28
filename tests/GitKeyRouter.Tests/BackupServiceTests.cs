using System.Security.Cryptography;
using System.Text.Json;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.Backup;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task CreateSnapshot_PublishesOnlyCompletedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var service = new BackupService(
            paths,
            new PhysicalFileSystem(),
            new FakeGitUrlRewriteStore(),
            new TestClock());

        var manifest = await service.CreateSnapshotAsync("atomic publish");

        Assert.True(Directory.Exists(manifest.BackupDirectory));
        Assert.True(File.Exists(Path.Combine(manifest.BackupDirectory, "manifest.json")));
        Assert.False(Path.GetFileName(manifest.BackupDirectory).StartsWith(".pending-", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateDirectories(paths.BackupRootDirectory, ".pending-*"));
    }

    [Fact]
    public async Task CreateSnapshot_CleansPendingDirectoryWhenPreparationFails()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new FaultingFileSystem { FailManifestWrite = true };
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());

        await Assert.ThrowsAsync<IOException>(() => service.CreateSnapshotAsync("preparation failure"));

        Assert.Empty(Directory.EnumerateDirectories(paths.BackupRootDirectory));
    }

    [Fact]
    public async Task CreateSnapshot_CleansPendingDirectoryWhenPublishFails()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new FaultingFileSystem { FailDirectoryMove = true };
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());

        await Assert.ThrowsAsync<IOException>(() => service.CreateSnapshotAsync("publish failure"));

        Assert.Empty(Directory.EnumerateDirectories(paths.BackupRootDirectory));
    }

    [Fact]
    public async Task ListAsync_IgnoresPendingDirectories()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var service = new BackupService(
            paths,
            new PhysicalFileSystem(),
            new FakeGitUrlRewriteStore(),
            new TestClock());
        var manifest = await service.CreateSnapshotAsync("completed");
        var pendingDirectory = Path.Combine(paths.BackupRootDirectory, ".pending-test");
        Directory.CreateDirectory(pendingDirectory);
        File.Copy(
            Path.Combine(manifest.BackupDirectory, "manifest.json"),
            Path.Combine(pendingDirectory, "manifest.json"));

        var backups = await service.ListAsync();

        var backup = Assert.Single(backups);
        Assert.Equal(manifest.BackupDirectory, backup.BackupDirectory);
    }

    [Fact]
    public async Task Inventory_ClassifiesCompletePendingDamagedUnsupportedAndUnknownDirectories()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var clock = new TestClock();
        var service = new BackupService(
            paths,
            new PhysicalFileSystem(),
            new FakeGitUrlRewriteStore(),
            clock);
        var complete = await service.CreateSnapshotAsync("complete");
        var pending = Path.Combine(paths.BackupRootDirectory, ".pending-abandoned");
        var damaged = Path.Combine(paths.BackupRootDirectory, "damaged");
        var unsupported = Path.Combine(paths.BackupRootDirectory, "unsupported");
        var unknown = Path.Combine(paths.BackupRootDirectory, "unknown");
        Directory.CreateDirectory(pending);
        Directory.SetLastWriteTimeUtc(pending, clock.UtcNow.AddHours(-2).UtcDateTime);
        Directory.CreateDirectory(damaged);
        await File.WriteAllTextAsync(Path.Combine(damaged, "manifest.json"), "{not-json");
        Directory.CreateDirectory(unsupported);
        await File.WriteAllTextAsync(
            Path.Combine(unsupported, "manifest.json"),
            "{\"SchemaVersion\":99,\"Reason\":\"future\"}");
        Directory.CreateDirectory(unknown);

        var inventory = await service.InventoryAsync();

        Assert.Equal(BackupHealthStatus.Complete, Find(inventory, complete.BackupDirectory).Status);
        var pendingItem = Find(inventory, pending);
        Assert.Equal(BackupHealthStatus.Pending, pendingItem.Status);
        Assert.True(pendingItem.CanClean);
        Assert.Equal(BackupHealthStatus.Damaged, Find(inventory, damaged).Status);
        Assert.Equal(BackupHealthStatus.Unsupported, Find(inventory, unsupported).Status);
        Assert.Equal(BackupHealthStatus.Unknown, Find(inventory, unknown).Status);
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyPreviewedInvalidDirectChildren()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var service = new BackupService(
            paths,
            new PhysicalFileSystem(),
            new FakeGitUrlRewriteStore(),
            new TestClock());
        var complete = await service.CreateSnapshotAsync("complete");
        var invalid = Path.Combine(paths.BackupRootDirectory, "missing-manifest");
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(invalid);
        Directory.CreateDirectory(outside);

        var plan = await service.PreviewCleanupAsync([invalid, complete.BackupDirectory, outside]);

        var target = Assert.Single(plan.Targets);
        Assert.Equal(Path.GetFullPath(invalid), target.BackupDirectory);
        Assert.Equal(2, plan.Rejected.Count);
        var result = await service.CleanAsync(plan);
        Assert.True(result.Success);
        Assert.False(Directory.Exists(invalid));
        Assert.True(Directory.Exists(complete.BackupDirectory));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Cleanup_ProtectsRecentPendingAndReparsePointDirectories()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new FaultingFileSystem();
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());
        Directory.CreateDirectory(paths.BackupRootDirectory);
        var pending = Path.Combine(paths.BackupRootDirectory, ".pending-recent");
        var reparse = Path.Combine(paths.BackupRootDirectory, "reparse");
        Directory.CreateDirectory(pending);
        Directory.CreateDirectory(reparse);
        fileSystem.ReparseDirectories.Add(Path.GetFullPath(reparse));

        var plan = await service.PreviewCleanupAsync([pending, reparse]);

        Assert.Empty(plan.Targets);
        Assert.Equal(2, plan.Rejected.Count);
        Assert.True(Directory.Exists(pending));
        Assert.True(Directory.Exists(reparse));
    }

    [Fact]
    public async Task Cleanup_RejectsChildrenWhenBackupRootIsAReparsePoint()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new FaultingFileSystem();
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());
        var invalid = Path.Combine(paths.BackupRootDirectory, "invalid");
        Directory.CreateDirectory(invalid);
        fileSystem.ReparseDirectories.Add(Path.GetFullPath(paths.BackupRootDirectory));

        var plan = await service.PreviewCleanupAsync([invalid]);

        Assert.Empty(plan.Targets);
        Assert.Single(plan.Rejected);
        Assert.True(Directory.Exists(invalid));
    }

    [Fact]
    public async Task Cleanup_ReportsDeletionFailureAndKeepsDirectory()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new FaultingFileSystem();
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());
        var invalid = Path.Combine(paths.BackupRootDirectory, "invalid");
        Directory.CreateDirectory(invalid);
        var plan = await service.PreviewCleanupAsync([invalid]);
        fileSystem.FailDirectoryDelete = true;

        var result = await service.CleanAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("Simulated directory deletion failure", string.Join(' ', result.Errors), StringComparison.Ordinal);
        Assert.True(Directory.Exists(invalid));
    }

    [Fact]
    public async Task Cleanup_RejectsTargetChangedAfterPreview()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var service = new BackupService(
            paths,
            new PhysicalFileSystem(),
            new FakeGitUrlRewriteStore(),
            new TestClock());
        var invalid = Path.Combine(paths.BackupRootDirectory, "invalid");
        Directory.CreateDirectory(invalid);
        var plan = await service.PreviewCleanupAsync([invalid]);
        await File.WriteAllTextAsync(Path.Combine(invalid, "manifest.json"), "{\"SchemaVersion\":99}");

        var result = await service.CleanAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("changed after preview", string.Join(' ', result.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(invalid));
    }

    [Fact]
    public async Task SnapshotAndRestore_PreservesAllThreeConfigurationTypes()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new PhysicalFileSystem();
        var git = new FakeGitUrlRewriteStore();
        var originalRule = new GitUrlRewriteRule("git@github-camus:camus0109/", "https://github.com/camus0109/");
        git.Rules.Add(originalRule);
        Directory.CreateDirectory(paths.AppDataDirectory);
        Directory.CreateDirectory(paths.SshDirectory);
        const string originalConfig = "{\"schemaVersion\":3,\"GitServices\":[],\"Identities\":[],\"RepositoryRoutes\":[],\"GitProfiles\":[],\"GitProfileRules\":[]}";
        await File.WriteAllTextAsync(paths.ConfigPath, originalConfig);
        await File.WriteAllTextAsync(paths.SshConfigPath, "# original ssh config");
        var service = new BackupService(paths, fileSystem, git, new TestClock());

        var manifest = await service.CreateSnapshotAsync("test snapshot");
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(3, manifest.AppConfigSchemaVersion);
        Assert.Equal(3, manifest.Files.Count);
        foreach (var (fileName, integrity) in manifest.Files)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(manifest.BackupDirectory, fileName));
            Assert.Equal(bytes.LongLength, integrity.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), integrity.Sha256);
        }

        await File.WriteAllTextAsync(paths.ConfigPath, "{\"changed\":true}");
        await File.WriteAllTextAsync(paths.SshConfigPath, "changed ssh config");
        git.Rules.Clear();
        git.Rules.Add(new GitUrlRewriteRule("git@wrong:", "https://github.com/"));

        Assert.True((await service.RestoreAppConfigAsync(manifest.BackupDirectory)).Success);
        Assert.True((await service.RestoreSshConfigAsync(manifest.BackupDirectory)).Success);
        Assert.True((await service.RestoreGitRewritesAsync(manifest.BackupDirectory)).Success);
        Assert.Equal(originalConfig, await File.ReadAllTextAsync(paths.ConfigPath));
        Assert.Equal("# original ssh config", await File.ReadAllTextAsync(paths.SshConfigPath));
        Assert.Contains(originalRule, git.Rules);
        Assert.DoesNotContain(git.Rules, item => item.BaseUrl == "git@wrong:");
    }

    [Fact]
    public async Task RestoreAppConfig_RejectsFutureSchemaWithoutChangingCurrentConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new PhysicalFileSystem();
        Directory.CreateDirectory(paths.AppDataDirectory);
        Directory.CreateDirectory(paths.BackupRootDirectory);
        const string currentConfig = "{\"SchemaVersion\":3,\"GitServices\":[],\"Identities\":[],\"RepositoryRoutes\":[],\"GitProfiles\":[],\"GitProfileRules\":[]}";
        await File.WriteAllTextAsync(paths.ConfigPath, currentConfig);
        var backupDirectory = Path.Combine(paths.BackupRootDirectory, "future");
        Directory.CreateDirectory(backupDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "manifest.json"),
            "{\"SchemaVersion\":1,\"BackupDirectory\":\"future\",\"CreatedAt\":\"2026-07-18T00:00:00Z\",\"Reason\":\"future\",\"AppConfigExisted\":true,\"AppConfigSchemaVersion\":99,\"SshConfigExisted\":false,\"GitRewriteCaptureSucceeded\":true,\"GitRewriteCount\":0}");
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "app_config.json"), "{\"schemaVersion\":99}");
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "git_url_rewrites.json"), "[]");
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());

        var result = await service.RestoreAppConfigAsync(backupDirectory);

        Assert.False(result.Success);
        Assert.Contains("supports up to schema", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(currentConfig, await File.ReadAllTextAsync(paths.ConfigPath));
    }

    [Fact]
    public async Task RestoreAppConfig_RejectsTamperedBackupWithoutChangingCurrentConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new PhysicalFileSystem();
        Directory.CreateDirectory(paths.AppDataDirectory);
        const string originalConfig = "{\"SchemaVersion\":3,\"GitServices\":[],\"Identities\":[],\"RepositoryRoutes\":[],\"GitProfiles\":[],\"GitProfileRules\":[]}";
        await File.WriteAllTextAsync(paths.ConfigPath, originalConfig);
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());
        var manifest = await service.CreateSnapshotAsync("tamper test");
        await File.AppendAllTextAsync(Path.Combine(manifest.BackupDirectory, "app_config.json"), "tampered");
        const string currentConfig = "{\"SchemaVersion\":3,\"changed\":true}";
        await File.WriteAllTextAsync(paths.ConfigPath, currentConfig);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(manifest.BackupDirectory));
        var result = await service.RestoreAppConfigAsync(manifest.BackupDirectory);

        Assert.False(result.Success);
        Assert.Contains("integrity", string.Join(' ', result.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(currentConfig, await File.ReadAllTextAsync(paths.ConfigPath));
    }

    [Fact]
    public async Task ReadAsync_RemainsCompatibleWithLegacySchemaOneManifest()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new PhysicalFileSystem();
        Directory.CreateDirectory(paths.AppDataDirectory);
        await File.WriteAllTextAsync(paths.ConfigPath, "{\"SchemaVersion\":3}");
        var service = new BackupService(paths, fileSystem, new FakeGitUrlRewriteStore(), new TestClock());
        var manifest = await service.CreateSnapshotAsync("legacy test");
        manifest.SchemaVersion = 1;
        manifest.Files.Clear();
        await File.WriteAllTextAsync(
            Path.Combine(manifest.BackupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        await File.AppendAllTextAsync(Path.Combine(manifest.BackupDirectory, "app_config.json"), " ");

        var snapshot = await service.ReadAsync(manifest.BackupDirectory);

        Assert.Equal(1, snapshot.Manifest.SchemaVersion);
        Assert.NotNull(snapshot.AppConfigText);
    }

    [Fact]
    public async Task RestoreGitRewrites_RollsBackAutomaticallyWhenApplyFails()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var fileSystem = new PhysicalFileSystem();
        var git = new FakeGitUrlRewriteStore();
        var targetRule = new GitUrlRewriteRule("git@target:", "https://target.example/");
        var originalRule = new GitUrlRewriteRule("git@original:", "https://original.example/");
        git.Rules.Add(targetRule);
        var service = new BackupService(paths, fileSystem, git, new TestClock());
        var selected = await service.CreateSnapshotAsync("selected target");
        git.Rules.Clear();
        git.Rules.Add(originalRule);
        git.FailNextAdd = true;

        var result = await service.RestoreGitRewritesAsync(selected.BackupDirectory);

        Assert.False(result.Success);
        Assert.Contains("restored automatically", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([originalRule], git.Rules);
    }

    private static BackupInventoryItem Find(
        IEnumerable<BackupInventoryItem> inventory,
        string directory)
        => Assert.Single(inventory, item =>
            string.Equals(item.BackupDirectory, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase));

    private sealed class FaultingFileSystem : IFileSystem
    {
        private readonly PhysicalFileSystem _inner = new();

        public bool FailManifestWrite { get; init; }

        public bool FailDirectoryMove { get; init; }

        public bool FailDirectoryDelete { get; set; }

        public HashSet<string> ReparseDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public FileAttributes GetAttributes(string path)
            => ReparseDirectories.Contains(Path.GetFullPath(path))
                ? _inner.GetAttributes(path) | FileAttributes.ReparsePoint
                : _inner.GetAttributes(path);

        public DateTimeOffset GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void DeleteDirectory(string path, bool recursive)
        {
            if (FailDirectoryDelete)
            {
                throw new IOException("Simulated directory deletion failure.");
            }

            _inner.DeleteDirectory(path, recursive);
        }

        public void MoveDirectory(string sourcePath, string destinationPath)
        {
            if (FailDirectoryMove)
            {
                throw new IOException("Simulated directory publication failure.");
            }

            _inner.MoveDirectory(sourcePath, destinationPath);
        }

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
            => _inner.ReadAllTextAsync(path, cancellationToken);

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
            => _inner.ReadAllBytesAsync(path, cancellationToken);

        public Task WriteAllTextAtomicAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            if (FailManifestWrite
                && string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Simulated manifest write failure.");
            }

            return _inner.WriteAllTextAtomicAsync(path, content, cancellationToken);
        }

        public Task WriteAllBytesAtomicAsync(
            string path,
            byte[] content,
            CancellationToken cancellationToken = default)
            => _inner.WriteAllBytesAtomicAsync(path, content, cancellationToken);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
            => _inner.CopyFile(sourcePath, destinationPath, overwrite);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
            => _inner.MoveFile(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public IEnumerable<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern)
            => _inner.EnumerateFiles(path, searchPattern);
    }
}
