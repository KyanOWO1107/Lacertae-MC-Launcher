using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Operations;

public interface IBackgroundOperation
{
    string Id { get; }
    string Kind { get; }
    Task<Result<Unit>> ExecuteAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}
