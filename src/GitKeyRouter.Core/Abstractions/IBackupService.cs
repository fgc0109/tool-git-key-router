using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Abstractions;

public interface IBackupService
{
    Task<BackupManifest> CreateSnapshotAsync(string reason, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupManifest>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupInventoryItem>> InventoryAsync(CancellationToken cancellationToken = default);

    Task<BackupCleanupPlan> PreviewCleanupAsync(
        IEnumerable<string> backupDirectories,
        CancellationToken cancellationToken = default);

    Task<OperationResult<IReadOnlyList<string>>> CleanAsync(
        BackupCleanupPlan plan,
        CancellationToken cancellationToken = default);

    Task<BackupSnapshot> ReadAsync(string backupDirectory, CancellationToken cancellationToken = default);

    Task<OperationResult> RestoreAppConfigAsync(string backupDirectory, CancellationToken cancellationToken = default);

    Task<OperationResult> RestoreSshConfigAsync(string backupDirectory, CancellationToken cancellationToken = default);

    Task<OperationResult> RestoreGitRewritesAsync(string backupDirectory, CancellationToken cancellationToken = default);
}
