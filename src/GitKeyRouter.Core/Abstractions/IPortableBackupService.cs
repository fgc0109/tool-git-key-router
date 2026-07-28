using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Abstractions;

public interface IPortableBackupService
{
    Task<OperationResult<PortableBackupPreview>> ExportAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken = default);

    Task<OperationResult<PortableBackupPreview>> InspectAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken = default);

    Task<OperationResult<PortableBackupImportResult>> ImportAsync(
        string packagePath,
        string password,
        CancellationToken cancellationToken = default);
}
