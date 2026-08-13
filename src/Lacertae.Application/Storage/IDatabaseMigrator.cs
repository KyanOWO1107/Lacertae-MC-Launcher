using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Storage;

public interface IDatabaseMigrator
{
    Task<Result<Unit>> MigrateAsync(CancellationToken cancellationToken);
}
