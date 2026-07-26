using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Abstractions;

public interface IAppConfigStore
{
    string ConfigPath { get; }

    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);

    Task<AppConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default);

    Task<AppConfigFileVersion> SaveIfUnchangedAsync(
        AppConfig config,
        AppConfigFileVersion expectedVersion,
        CancellationToken cancellationToken = default);
}
