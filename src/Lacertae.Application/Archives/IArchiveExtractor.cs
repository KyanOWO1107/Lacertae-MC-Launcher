using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Archives;

public interface IArchiveExtractor
{
    Task<Result<Unit>> ExtractAsync(
        ArchiveExtractionRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken);
}
