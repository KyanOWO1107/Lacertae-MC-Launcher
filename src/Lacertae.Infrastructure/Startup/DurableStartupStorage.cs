using Lacertae.Application.Accounts;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Install;
using Lacertae.Application.Settings;
using Lacertae.Application.Startup;
using Lacertae.Application.Storage;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;
using Lacertae.Infrastructure.Accounts;
using Lacertae.Infrastructure.GameRoots;
using Lacertae.Infrastructure.Install;
using Lacertae.Infrastructure.Settings;
using Lacertae.Infrastructure.Storage;
using Lacertae.Infrastructure.Versions;

namespace Lacertae.Infrastructure.Startup;

public sealed class DurableStartupStorageFactory(Func<DataRoot, ISecretVault?>? secretVaultFactory = null) : IStartupStorageFactory
{
    private readonly Func<DataRoot, ISecretVault?>? secretVaultFactory = secretVaultFactory;

    public IStartupStorage Create(DataRoot dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        return new DurableStartupStorage(dataRoot, secretVaultFactory?.Invoke(dataRoot));
    }
}

internal sealed class DurableStartupStorage : IStartupStorage
{
    private readonly JsonSettingsRepository settingsRepository;
    private readonly SqliteMigrator migrator;
    private readonly SqliteGameRootRepository gameRootRepository;
    private readonly RefreshGameRootAvailability refreshGameRoots;
    private readonly RenameVersionFolder renameVersionFolder;
    private readonly RecoverAccountDeletions recoverAccountDeletions;
    private readonly RecoverVanillaInstalls recoverVanillaInstalls;

    public DurableStartupStorage(DataRoot dataRoot, ISecretVault? secretVault = null)
    {
        SystemFileSystem fileSystem = new();
        settingsRepository = new JsonSettingsRepository(dataRoot.SettingsPath);
        SqliteConnectionFactory connectionFactory = new(dataRoot.DatabasePath);
        migrator = new SqliteMigrator(connectionFactory);
        gameRootRepository = new SqliteGameRootRepository(connectionFactory);
        refreshGameRoots = new RefreshGameRootAvailability(gameRootRepository, fileSystem);
        SqliteAccountRepository accountRepository = new(connectionFactory);
        recoverAccountDeletions = new RecoverAccountDeletions(
            accountRepository,
            new DeleteAccount(accountRepository, secretVault, settingsRepository));
        SqliteVersionOverrideRepository versionOverrides = new(connectionFactory);
        JsonVersionRenameJournal journal = new(Path.Combine(dataRoot.LocalPath, "version-rename.journal.json"));
        renameVersionFolder = new RenameVersionFolder(versionOverrides, journal);
        recoverVanillaInstalls = new RecoverVanillaInstalls(
            new SqliteInstallJournalRepository(connectionFactory),
            new StreamingGameFileVerifier());
    }

    public Task<Result<LauncherSettings>> LoadSettingsAsync(CancellationToken cancellationToken) =>
        settingsRepository.LoadAsync(cancellationToken);

    public Task<Result<Unit>> MigrateDatabaseAsync(CancellationToken cancellationToken) =>
        migrator.MigrateAsync(cancellationToken);

    public Task<Result<Unit>> RecoverVersionRenameAsync(CancellationToken cancellationToken) =>
        renameVersionFolder.RecoverAsync(cancellationToken);

    public Task<Result<Unit>> RecoverAccountDeletionsAsync(CancellationToken cancellationToken) =>
        recoverAccountDeletions.ExecuteAsync(cancellationToken);

    public Task<Result<Unit>> RecoverVanillaInstallsAsync(CancellationToken cancellationToken) =>
        recoverVanillaInstalls.ExecuteAsync(new Progress<Lacertae.Domain.Operations.OperationProgress>(), cancellationToken);

    public async Task<Result<IReadOnlyList<GameRoot>>> RefreshGameRootsAsync(CancellationToken cancellationToken)
    {
        Result<Unit> refreshed = await refreshGameRoots.ExecuteAsync(cancellationToken);
        if (!refreshed.IsSuccess)
        {
            return Result<IReadOnlyList<GameRoot>>.Failure(refreshed.Problem!);
        }

        try
        {
            return Result<IReadOnlyList<GameRoot>>.Success(await gameRootRepository.GetAllAsync(cancellationToken));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return Result<IReadOnlyList<GameRoot>>.Failure(new Lacertae.Domain.Problems.Problem(
                "DATABASE_READ_FAILED",
                Lacertae.Domain.Problems.ProblemStage.Storage,
                "problem.database.read_failed",
                true,
                Guid.NewGuid().ToString("N"),
                ["action.database.retry"]));
        }
    }
}
