using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Operations;

public interface IBackgroundTaskStore
{
    Task<Result<IReadOnlyList<OperationSnapshot>>> GetActiveAsync(
        CancellationToken cancellationToken);

    Task<Result<Unit>> SaveAsync(
        BackgroundTaskRecord record,
        CancellationToken cancellationToken);
}
