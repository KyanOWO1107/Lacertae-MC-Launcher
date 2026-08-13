using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

namespace Lacertae.Application.Settings;

public sealed class SettingsService(ISettingsRepository repository)
{
    public Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken) =>
        repository.LoadAsync(cancellationToken);

    public Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken) =>
        repository.SaveAsync(settings, cancellationToken);
}
