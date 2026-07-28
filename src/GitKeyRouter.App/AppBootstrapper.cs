using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.Backup;
using GitKeyRouter.Infrastructure.Configuration;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Infrastructure.Git;
using GitKeyRouter.Infrastructure.Logging;
using GitKeyRouter.Infrastructure.ProcessExecution;

namespace GitKeyRouter.App;

public static class AppBootstrapper
{
    public static ApplicationServices CreateServices()
    {
        IAppPaths paths = new AppPaths();
        IFileSystem fileSystem = new PhysicalFileSystem();
        IClock clock = new SystemClock();
        IProcessRunner processRunner = new ProcessRunner();
        IToolchainService toolchainService = new ToolchainService(processRunner);
        IRequiredToolInstallerService requiredToolInstallerService = new RequiredToolInstallerService(
            toolchainService,
            processRunner);
        var gitStore = new GitUrlRewriteStore(processRunner, toolchainService);
        IBackupService backupService = new BackupService(paths, fileSystem, gitStore, clock);
        IAppConfigStore configStore = new JsonAppConfigStore(paths, fileSystem);
        ISafeLogger logger = new SafeFileLogger(paths);
        var gitProviderAdapters = GitProviderAdapterRegistry.CreateDefault();

        var sshConfigService = new SshConfigService(fileSystem, paths, backupService, gitProviderAdapters);
        var identityService = new IdentityService(configStore, backupService, clock, sshConfigService);
        var gitServiceService = new GitServiceService(
            configStore,
            backupService,
            processRunner,
            toolchainService,
            gitProviderAdapters);
        var gitProfileService = new GitProfileService(
            configStore,
            backupService,
            fileSystem,
            paths,
            processRunner,
            toolchainService);
        IPortableBackupService portableBackupService = new PortableBackupService(
            paths,
            fileSystem,
            configStore,
            gitStore,
            backupService,
            gitProfileService,
            clock);
        var ownerRouteService = new OwnerRouteService(configStore, backupService, gitProviderAdapters);
        var gitUrlRewriteService = new GitUrlRewriteService(
            configStore,
            gitStore,
            backupService,
            gitProviderAdapters);
        var sshKeyService = new SshKeyService(
            fileSystem,
            processRunner,
            toolchainService,
            clock,
            gitProviderAdapters);
        var sshKeyRenameService = new SshKeyRenameService(
            fileSystem,
            configStore,
            backupService,
            sshConfigService,
            sshKeyService,
            clock);
        var diagnosticService = new DiagnosticService(
            configStore,
            paths,
            fileSystem,
            toolchainService,
            sshConfigService,
            gitUrlRewriteService,
            clock,
            gitProviderAdapters);

        return new ApplicationServices
        {
            Paths = paths,
            FileSystem = fileSystem,
            ConfigStore = configStore,
            ToolchainService = toolchainService,
            RequiredToolInstallerService = requiredToolInstallerService,
            BackupService = backupService,
            PortableBackupService = portableBackupService,
            GitProviderAdapters = gitProviderAdapters,
            GitServiceService = gitServiceService,
            GitProfileService = gitProfileService,
            IdentityService = identityService,
            OwnerRouteService = ownerRouteService,
            SshKeyService = sshKeyService,
            SshKeyRenameService = sshKeyRenameService,
            SshConfigService = sshConfigService,
            GitUrlRewriteService = gitUrlRewriteService,
            DiagnosticService = diagnosticService,
            Logger = logger
        };
    }
}
