using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

namespace Lacertae.Application.Settings;

public interface ISettingsRepository
{
    Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken);
    Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken);
}
