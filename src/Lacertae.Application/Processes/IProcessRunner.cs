using Lacertae.Domain.Results;

namespace Lacertae.Application.Processes;

public interface IProcessRunner
{
    Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}
