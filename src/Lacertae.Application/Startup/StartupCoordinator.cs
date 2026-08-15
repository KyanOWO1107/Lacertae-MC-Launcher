using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;

namespace Lacertae.Application.Startup;

public interface IStartupDataRootResolver
{
    Result<DataRoot> Resolve();
}

public interface IStartupLoggingInitializer : IDisposable
{
    Result<Unit> Initialize(DataRoot dataRoot);
}

public interface IStartupStorageFactory
{
    IStartupStorage Create(DataRoot dataRoot);
}

public interface IStartupStorage
{
    Task<Result<LauncherSettings>> LoadSettingsAsync(CancellationToken cancellationToken);
    Task<Result<Unit>> MigrateDatabaseAsync(CancellationToken cancellationToken);
    Task<Result<Unit>> RecoverVersionRenameAsync(CancellationToken cancellationToken);
    Task<Result<Unit>> RecoverAccountDeletionsAsync(CancellationToken cancellationToken);
    Task<Result<Unit>> RecoverVanillaInstallsAsync(CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<GameRoot>>> RefreshGameRootsAsync(CancellationToken cancellationToken);
}

public sealed class StartupCoordinator(
    IStartupDataRootResolver dataRootResolver,
    IStartupLoggingInitializer loggingInitializer,
    IStartupStorageFactory storageFactory)
{
    public async Task<Result<StartupState>> InitializeAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataRootResolver);
        ArgumentNullException.ThrowIfNull(loggingInitializer);
        ArgumentNullException.ThrowIfNull(storageFactory);

        Result<DataRoot> resolved = dataRootResolver.Resolve();
        if (!resolved.IsSuccess)
        {
            return Result<StartupState>.Failure(resolved.Problem!);
        }

        DataRoot dataRoot = resolved.Value;
        Result<Unit> logging = loggingInitializer.Initialize(dataRoot);
        if (!logging.IsSuccess)
        {
            return Result<StartupState>.Failure(logging.Problem!);
        }

        IStartupStorage storage = storageFactory.Create(dataRoot);
        Result<LauncherSettings> settings = await storage.LoadSettingsAsync(cancellationToken);
        if (!settings.IsSuccess)
        {
            return Result<StartupState>.Failure(settings.Problem!);
        }

        Result<Unit> migrated = await storage.MigrateDatabaseAsync(cancellationToken);
        if (!migrated.IsSuccess)
        {
            return Result<StartupState>.Failure(migrated.Problem!);
        }

        Result<Unit> recovered = await storage.RecoverVersionRenameAsync(cancellationToken);
        if (!recovered.IsSuccess)
        {
            return Result<StartupState>.Failure(recovered.Problem!);
        }

        Result<Unit> recoveredAccounts = await storage.RecoverAccountDeletionsAsync(cancellationToken);
        if (!recoveredAccounts.IsSuccess)
        {
            return Result<StartupState>.Failure(recoveredAccounts.Problem!);
        }

        Result<Unit> recoveredInstalls = await storage.RecoverVanillaInstallsAsync(cancellationToken);
        if (!recoveredInstalls.IsSuccess)
        {
            return Result<StartupState>.Failure(recoveredInstalls.Problem!);
        }

        Result<IReadOnlyList<GameRoot>> roots = await storage.RefreshGameRootsAsync(cancellationToken);
        if (!roots.IsSuccess)
        {
            return Result<StartupState>.Failure(roots.Problem!);
        }

        return Result<StartupState>.Success(new StartupState(dataRoot, settings.Value, roots.Value));
    }
}
