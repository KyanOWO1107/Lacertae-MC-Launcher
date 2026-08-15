using Lacertae.Application.Versions;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Accounts;

public sealed class SetVersionAccount(
    IAccountRepository accountRepository,
    IVersionOverrideRepository versionOverrideRepository)
{
    public async Task<Result<Unit>> ExecuteAsync(
        string gameRootId,
        string versionFolder,
        string accountId,
        CancellationToken cancellationToken)
    {
        Account? account = await accountRepository.GetAsync(accountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
        {
            return Result<Unit>.Failure(AccountProblem.Required());
        }

        IReadOnlyList<VersionOverride> overrides = await versionOverrideRepository
            .GetForGameRootAsync(gameRootId, cancellationToken);
        VersionOverride? existing = overrides.FirstOrDefault(item =>
            string.Equals(item.VersionFolder, versionFolder, StringComparison.Ordinal));
        VersionOverride updated = existing is null
            ? new VersionOverride(gameRootId, versionFolder, null, IsolationOverride.Inherit, accountId, null, null, null, null, [], [])
            : existing with { AccountId = accountId };
        return await versionOverrideRepository.UpsertAsync(updated, cancellationToken);
    }
}
