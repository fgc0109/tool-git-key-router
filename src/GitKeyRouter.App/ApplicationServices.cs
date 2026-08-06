using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.GitHub;

namespace GitKeyRouter.App;

public sealed class ApplicationServices
{
    public required IAppPaths Paths { get; init; }

    public required IFileSystem FileSystem { get; init; }

    public required IAppConfigStore ConfigStore { get; init; }

    public required IToolchainService ToolchainService { get; init; }

    public required IRequiredToolInstallerService RequiredToolInstallerService { get; init; }

    public required IBackupService BackupService { get; init; }

    public required IPortableBackupService PortableBackupService { get; init; }

    public required GitProviderAdapterRegistry GitProviderAdapters { get; init; }

    public required GitServiceService GitServiceService { get; init; }

    public required GitProfileService GitProfileService { get; init; }

    public required IdentityService IdentityService { get; init; }

    public required OwnerRouteService OwnerRouteService { get; init; }

    public required SshKeyService SshKeyService { get; init; }

    public required SshHostTrustService SshHostTrustService { get; init; }

    public required GitSshBackendService GitSshBackendService { get; init; }

    public required SshKeyRenameService SshKeyRenameService { get; init; }

    public required SshConfigService SshConfigService { get; init; }

    public required GitUrlRewriteService GitUrlRewriteService { get; init; }

    public required GitHubCliService GitHubCliService { get; init; }

    public required DiagnosticService DiagnosticService { get; init; }

    public required ISafeLogger Logger { get; init; }
}
