using Lacertae.Domain.Java;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Java;

public interface IManagedJavaCatalog
{
    Task<Result<ManagedJavaPackage>> GetPackageAsync(
        string component,
        JavaArchitecture architecture,
        CancellationToken cancellationToken);
}
