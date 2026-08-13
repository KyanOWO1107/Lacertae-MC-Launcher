using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public interface IAccountRepository
{
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken);
    Task<Account?> GetAsync(string accountId, CancellationToken cancellationToken);
    Task<Account?> FindByIdentityAsync(AccountIdentity identity, CancellationToken cancellationToken);
    Task<Result<Unit>> UpsertAsync(Account account, CancellationToken cancellationToken);
    Task<Result<Unit>> SetStatusAsync(string accountId, AccountStatus status, CancellationToken cancellationToken);
    Task<Result<Unit>> DeleteAndClearVersionReferencesAsync(string accountId, CancellationToken cancellationToken);
}
