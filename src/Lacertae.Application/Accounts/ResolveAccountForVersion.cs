using Lacertae.Application.Settings;
using Lacertae.Application.Versions;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class ResolveAccountForVersion(
    IAccountRepository accountRepository,
    ISettingsRepository settingsRepository,
    IVersionOverrideRepository versionOverrideRepository)
{
    public async Task<Result<Account>> ExecuteAsync(
        string gameRootId,
        string versionFolder,
        CancellationToken cancellationToken)
    {
        Result<Domain.Settings.LauncherSettings> settings = await settingsRepository.LoadAsync(cancellationToken);
        if (!settings.IsSuccess)
        {
            return Result<Account>.Failure(settings.Problem!);
        }

        IReadOnlyList<Domain.Versions.VersionOverride> overrides = await versionOverrideRepository
            .GetForGameRootAsync(gameRootId, cancellationToken);
        Domain.Versions.VersionOverride? versionOverride = overrides.FirstOrDefault(item =>
            string.Equals(item.VersionFolder, versionFolder, StringComparison.Ordinal));
        string? accountId = versionOverride?.AccountId ?? settings.Value.DefaultAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Result<Account>.Failure(AccountProblem.Required());
        }

        Account? account = await accountRepository.GetAsync(accountId, cancellationToken);
        return account is not null && account.Status == AccountStatus.Active
            ? Result<Account>.Success(account)
            : Result<Account>.Failure(AccountProblem.Required());
    }
}
