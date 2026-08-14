using Lacertae.Application.Archives;
using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Testing.Archives;

public sealed class FakeArchiveExtractor : IArchiveExtractor
{
    public List<ArchiveExtractionRequest> Requests { get; } = [];

    public Result<Unit> Result { get; set; } = Lacertae.Domain.Results.Result.Success();

    public Task<Result<Unit>> ExtractAsync(
        ArchiveExtractionRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}
