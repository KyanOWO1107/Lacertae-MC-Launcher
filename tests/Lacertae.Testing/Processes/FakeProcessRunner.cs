using Lacertae.Application.Processes;
using Lacertae.Domain.Results;

namespace Lacertae.Testing.Processes;

public sealed class FakeProcessRunner : IProcessRunner
{
    public ProcessRequest? LastRequest { get; private set; }

    public Result<ProcessResult> Response { get; set; } =
        Result<ProcessResult>.Success(new ProcessResult(0, string.Empty, string.Empty, false));

    public Task<Result<ProcessResult>> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Response);
    }
}
