using Lacertae.Domain.Java;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Java;

public interface IJavaProbe
{
    Task<Result<JavaInstallation>> ProbeAsync(
        string executablePath,
        JavaSource source,
        bool isManaged,
        CancellationToken cancellationToken);
}
