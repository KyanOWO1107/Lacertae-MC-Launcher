using Lacertae.Application.Settings;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class DeleteAccount(
    IAccountRepository repository,
    ISecretVault? secretVault,
    ISettingsRepository settingsRepository)
{
    public async Task<Result<Unit>> ExecuteAsync(string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Result<Unit>.Failure(AccountProblem.Required());
        }

        Account? account = await repository.GetAsync(accountId, cancellationToken);
        if (account is null)
        {
            return Result<Unit>.Failure(AccountProblem.Required());
        }

        if (account.Status != AccountStatus.Deleting)
        {
            Result<Unit> marked = await repository.SetStatusAsync(
                account.Id,
                AccountStatus.Deleting,
                cancellationToken);
            if (!marked.IsSuccess)
            {
                return marked;
            }
        }

        if (account.Type != AccountType.Offline && !string.IsNullOrWhiteSpace(account.SecretRef))
        {
            if (secretVault is null)
            {
                return Failure("SECRET_PLATFORM_UNAVAILABLE");
            }

            Result<Unit> secretDeleted = await secretVault.DeleteAsync(
                account.SecretRef,
                cancellationToken);
            if (!secretDeleted.IsSuccess)
            {
                return secretDeleted;
            }
        }

        Result<Domain.Settings.LauncherSettings> settings = await settingsRepository.LoadAsync(cancellationToken);
        if (!settings.IsSuccess)
        {
            return Result<Unit>.Failure(settings.Problem!);
        }

        if (string.Equals(settings.Value.DefaultAccountId, account.Id, StringComparison.Ordinal))
        {
            Result<Unit> settingsSaved = await settingsRepository.SaveAsync(
                settings.Value with { DefaultAccountId = null },
                cancellationToken);
            if (!settingsSaved.IsSuccess)
            {
                return settingsSaved;
            }
        }

        return await repository.DeleteAndClearVersionReferencesAsync(account.Id, cancellationToken);
    }

    private static Result<Unit> Failure(string code) => Result<Unit>.Failure(new Problem(
        code,
        ProblemStage.Authentication,
        "problem.auth.secret_vault_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.auth.retry"]));
}
