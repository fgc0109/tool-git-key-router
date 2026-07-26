using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class SshKeyRenameServiceTests
{
    private const string OpenSsh = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4f test@example.com";

    [Fact]
    public async Task RenamesKeyFilesAndUpdatesEverySharedIdentityAndSshConfig()
    {
        using var directory = new TemporaryDirectory();
        var paths = new TestAppPaths(directory.Path);
        Directory.CreateDirectory(paths.SshDirectory);
        var oldPrivatePath = Path.Combine(paths.SshDirectory, "id_ed25519_shared");
        var oldPublicPath = oldPrivatePath + ".pub";
        await File.WriteAllTextAsync(oldPrivatePath, "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret");
        await File.WriteAllTextAsync(oldPublicPath, OpenSsh);

        var identities = new List<GitIdentity>
        {
            Identity("one", "Account One", "github-one", oldPrivatePath, oldPublicPath),
            Identity("two", "Account Two", "github-two", oldPrivatePath, oldPublicPath)
        };
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig { Identities = identities }
        };
        var fileSystem = new PhysicalFileSystem();
        var backupService = new NoOpBackupService();
        var sshConfigService = new SshConfigService(fileSystem, paths, backupService);
        var initialSsh = sshConfigService.PreviewSynchronizeAll(string.Empty, identities).UpdatedText;
        await File.WriteAllTextAsync(paths.SshConfigPath, initialSsh);
        var clock = new TestClock();
        var keyService = new SshKeyService(
            fileSystem,
            new StubProcessRunner(_ => new ProcessResult { ExecutablePath = "ssh-keygen.exe", ExitCode = 0 }),
            new FixedToolchainService("git.exe", "ssh-keygen.exe"),
            clock);
        var service = new SshKeyRenameService(
            fileSystem,
            configStore,
            backupService,
            sshConfigService,
            keyService,
            clock);

        var planResult = await service.BuildPlanAsync("one", "id_ed25519_renamed");

        Assert.True(planResult.Success);
        var plan = Assert.IsType<SshKeyRenamePlan>(planResult.Value);
        Assert.Equal(2, plan.AffectedIdentityIds.Count);

        var result = await service.ApplyAsync(plan);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.False(File.Exists(oldPrivatePath));
        Assert.False(File.Exists(oldPublicPath));
        Assert.True(File.Exists(plan.NewPrivateKeyPath));
        Assert.True(File.Exists(plan.NewPublicKeyPath));
        Assert.Equal(2, result.Value.BackupFiles.Count);
        Assert.All(result.Value.BackupFiles, path => Assert.True(File.Exists(path)));
        Assert.All(configStore.Config.Identities, identity =>
        {
            Assert.Equal(plan.NewPrivateKeyPath, identity.PrivateKeyPath);
            Assert.Equal(plan.NewPublicKeyPath, identity.PublicKeyPath);
        });
        var updatedSsh = await File.ReadAllTextAsync(paths.SshConfigPath);
        Assert.Contains("id_ed25519_renamed", updatedSsh, StringComparison.Ordinal);
        Assert.DoesNotContain("id_ed25519_shared", updatedSsh, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsExistingDestinationWithoutMovingOrUpdatingAnything()
    {
        using var directory = new TemporaryDirectory();
        var paths = new TestAppPaths(directory.Path);
        Directory.CreateDirectory(paths.SshDirectory);
        var oldPrivatePath = Path.Combine(paths.SshDirectory, "id_ed25519_original");
        var oldPublicPath = oldPrivatePath + ".pub";
        var conflictingPath = Path.Combine(paths.SshDirectory, "id_ed25519_existing");
        await File.WriteAllTextAsync(oldPrivatePath, "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret");
        await File.WriteAllTextAsync(oldPublicPath, OpenSsh);
        await File.WriteAllTextAsync(conflictingPath, "do not replace");
        var identity = Identity("one", "Account One", "github-one", oldPrivatePath, oldPublicPath);
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig { Identities = [identity] }
        };
        var fileSystem = new PhysicalFileSystem();
        var backupService = new NoOpBackupService();
        var sshConfigService = new SshConfigService(fileSystem, paths, backupService);
        var clock = new TestClock();
        var keyService = new SshKeyService(
            fileSystem,
            new StubProcessRunner(_ => new ProcessResult { ExecutablePath = "ssh-keygen.exe", ExitCode = 0 }),
            new FixedToolchainService("git.exe", "ssh-keygen.exe"),
            clock);
        var service = new SshKeyRenameService(
            fileSystem,
            configStore,
            backupService,
            sshConfigService,
            keyService,
            clock);

        var result = await service.BuildPlanAsync(identity.Id, "id_ed25519_existing");

        Assert.False(result.Success);
        Assert.True(File.Exists(oldPrivatePath));
        Assert.True(File.Exists(oldPublicPath));
        Assert.Equal("do not replace", await File.ReadAllTextAsync(conflictingPath));
        Assert.Equal(oldPrivatePath, configStore.Config.Identities[0].PrivateKeyPath);
        Assert.Equal(0, backupService.SnapshotCount);
    }

    [Fact]
    public async Task PreservesCustomServiceHostPortAndUserInPreviewAndAppliedSshConfig()
    {
        using var directory = new TemporaryDirectory();
        var paths = new TestAppPaths(directory.Path);
        Directory.CreateDirectory(paths.SshDirectory);
        var oldPrivatePath = Path.Combine(paths.SshDirectory, "id_ed25519_gitea");
        var oldPublicPath = oldPrivatePath + ".pub";
        await File.WriteAllTextAsync(oldPrivatePath, "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret");
        await File.WriteAllTextAsync(oldPublicPath, OpenSsh);

        var gitea = new GitServiceInstance
        {
            Id = "gitea-cloud",
            DisplayName = "Gitea Cloud",
            ProviderKind = GitProviderKind.Gitea,
            HostName = "git.policoil.top",
            SshPort = 2222,
            SshUser = "forge",
            WebBaseUrl = "https://git.policoil.top"
        };
        var identity = Identity("one", "Gitea Account", "gitea-cloud-one", oldPrivatePath, oldPublicPath);
        identity.ServiceInstanceId = gitea.Id;
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom(), gitea],
                Identities = [identity]
            }
        };
        var fileSystem = new PhysicalFileSystem();
        var backupService = new NoOpBackupService();
        var sshConfigService = new SshConfigService(fileSystem, paths, backupService);
        var initialSsh = sshConfigService.PreviewUpsert(string.Empty, gitea, identity).UpdatedText;
        await File.WriteAllTextAsync(paths.SshConfigPath, initialSsh);
        var clock = new TestClock();
        var keyService = new SshKeyService(
            fileSystem,
            new StubProcessRunner(_ => new ProcessResult { ExecutablePath = "ssh-keygen.exe", ExitCode = 0 }),
            new FixedToolchainService("git.exe", "ssh-keygen.exe"),
            clock);
        var service = new SshKeyRenameService(
            fileSystem,
            configStore,
            backupService,
            sshConfigService,
            keyService,
            clock);

        var planResult = await service.BuildPlanAsync(identity.Id, "id_ed25519_gitea_renamed");

        Assert.True(planResult.Success);
        var plan = Assert.IsType<SshKeyRenamePlan>(planResult.Value);
        Assert.Contains("id_ed25519_gitea_renamed", plan.SshConfigDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("HostName github.com", plan.SshConfigDiff, StringComparison.Ordinal);

        var result = await service.ApplyAsync(plan);

        Assert.True(result.Success);
        var updatedSsh = await File.ReadAllTextAsync(paths.SshConfigPath);
        Assert.Contains("HostName git.policoil.top", updatedSsh, StringComparison.Ordinal);
        Assert.Contains("Port 2222", updatedSsh, StringComparison.Ordinal);
        Assert.Contains("User forge", updatedSsh, StringComparison.Ordinal);
        Assert.DoesNotContain("HostName github.com", updatedSsh, StringComparison.Ordinal);
        Assert.Contains("id_ed25519_gitea_renamed", updatedSsh, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsRenameWhenAnAffectedIdentityReferencesMissingService()
    {
        using var directory = new TemporaryDirectory();
        var paths = new TestAppPaths(directory.Path);
        Directory.CreateDirectory(paths.SshDirectory);
        var privatePath = Path.Combine(paths.SshDirectory, "id_ed25519_missing_service");
        var publicPath = privatePath + ".pub";
        await File.WriteAllTextAsync(privatePath, "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret");
        await File.WriteAllTextAsync(publicPath, OpenSsh);
        var identity = Identity("one", "Missing Service", "missing-one", privatePath, publicPath);
        identity.ServiceInstanceId = "missing-service";
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitServices = [GitServiceInstance.CreateGitHubCom()],
                Identities = [identity]
            }
        };
        var fileSystem = new PhysicalFileSystem();
        var backupService = new NoOpBackupService();
        var sshConfigService = new SshConfigService(fileSystem, paths, backupService);
        var clock = new TestClock();
        var keyService = new SshKeyService(
            fileSystem,
            new StubProcessRunner(_ => new ProcessResult { ExecutablePath = "ssh-keygen.exe", ExitCode = 0 }),
            new FixedToolchainService("git.exe", "ssh-keygen.exe"),
            clock);
        var service = new SshKeyRenameService(
            fileSystem,
            configStore,
            backupService,
            sshConfigService,
            keyService,
            clock);

        var result = await service.BuildPlanAsync(identity.Id, "id_ed25519_renamed");

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(privatePath));
        Assert.True(File.Exists(publicPath));
        Assert.Equal(0, backupService.SnapshotCount);
    }

    private static GitIdentity Identity(
        string id,
        string displayName,
        string hostAlias,
        string privateKeyPath,
        string publicKeyPath)
        => new()
        {
            Id = id,
            DisplayName = displayName,
            ServiceInstanceId = GitServiceInstance.GitHubComId,
            AccountName = id,
            HostAlias = hostAlias,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            CreatedAt = DateTimeOffset.UtcNow
        };
}
