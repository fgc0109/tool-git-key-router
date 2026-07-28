using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Abstractions;

public interface IGitProfileMaterializer
{
    Task<OperationResult> ApplyCurrentAsync(CancellationToken cancellationToken = default);
}
