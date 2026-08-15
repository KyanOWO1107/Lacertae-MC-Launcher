using Lacertae.Application.Settings;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class SetDefaultAccount(IAccountRepository accountRepository, ISettingsRepository settingsRepository)
{
    public async Task<Result<Unit>> ExecuteAsync(string accountId, CancellationToken cancellationToken)
    {
        Account? account = await accountRepository.GetAsync(accountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
        {
            return Result<Unit>.Failure(AccountProblem.Required());
        }

        Result<Domain.Settings.LauncherSettings> settings = await settingsRepository.LoadAsync(cancellationToken);
        if (!settings.IsSuccess)
        {
            return Result<Unit>.Failure(settings.Problem!);
        }

        return await settingsRepository.SaveAsync(settings.Value with { DefaultAccountId = accountId }, cancellationToken);
    }
}
