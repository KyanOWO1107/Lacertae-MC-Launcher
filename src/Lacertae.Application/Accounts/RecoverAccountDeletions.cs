using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class RecoverAccountDeletions(
    IAccountRepository repository,
    DeleteAccount deleteAccount)
{
    public async Task<Result<Unit>> ExecuteAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Domain.Accounts.Account> accounts = await repository.GetAllAsync(cancellationToken);
        foreach (Domain.Accounts.Account account in accounts.Where(static account =>
                     account.Status == Domain.Accounts.AccountStatus.Deleting))
        {
            Result<Unit> result = await deleteAccount.ExecuteAsync(account.Id, cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Success();
    }
}
