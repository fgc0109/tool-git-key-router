using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed class IdentityService
{
    private readonly IAppConfigStore _configStore;
    private readonly IBackupService _backupService;
    private readonly IClock _clock;
    private readonly SshConfigService? _sshConfigService;

    public IdentityService(
        IAppConfigStore configStore,
        IBackupService backupService,
        IClock clock,
        SshConfigService? sshConfigService = null)
    {
        _configStore = configStore;
        _backupService = backupService;
        _clock = clock;
        _sshConfigService = sshConfigService;
    }

    public async Task<IReadOnlyList<GitIdentity>> ListAsync(CancellationToken cancellationToken = default)
        => (await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Identities
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public async Task<OperationResult<GitIdentity>> SaveAsync(GitIdentity identity, CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var unmanagedHostAliases = _sshConfigService is null
            ? []
            : _sshConfigService.ParseUnmanagedHostAliases(
                await _sshConfigService.ReadRawAsync(cancellationToken).ConfigureAwait(false));
        var validation = IdentityValidator.Validate(identity, config, unmanagedHostAliases);
        if (!validation.IsValid)
        {
            return OperationResult<GitIdentity>.Fail("Identity validation failed.", validation.Errors.ToArray());
        }

        var existingIndex = config.Identities.FindIndex(item => string.Equals(item.Id, identity.Id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(identity.Id))
        {
            identity.Id = Guid.NewGuid().ToString("N");
        }

        if (identity.CreatedAt == default)
        {
            identity.CreatedAt = _clock.UtcNow;
        }

        await _backupService.CreateSnapshotAsync($"Save identity: {identity.DisplayName}", cancellationToken).ConfigureAwait(false);
        if (existingIndex >= 0)
        {
            config.Identities[existingIndex] = identity;
        }
        else
        {
            config.Identities.Add(identity);
        }

        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult<GitIdentity>.Ok(identity, "Identity saved.");
    }

    public async Task<OperationResult> DeleteAsync(string identityId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _configStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var identity = config.Identities.FirstOrDefault(item => string.Equals(item.Id, identityId, StringComparison.OrdinalIgnoreCase));
        if (identity is null)
        {
            return OperationResult.Fail("Identity was not found.");
        }

        await _backupService.CreateSnapshotAsync($"Delete identity record: {identity.DisplayName}", cancellationToken).ConfigureAwait(false);
        config.Identities.Remove(identity);
        foreach (var route in config.OwnerRoutes.Where(item => string.Equals(item.IdentityId, identityId, StringComparison.OrdinalIgnoreCase)))
        {
            route.Enabled = false;
        }

        await _configStore.SaveIfUnchangedAsync(config, snapshot.Version, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Identity record deleted. Key files were not deleted; related routes were disabled.");
    }
}
