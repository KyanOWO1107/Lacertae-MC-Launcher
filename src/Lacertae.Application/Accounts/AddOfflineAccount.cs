using Lacertae.Domain.Accounts;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class AddOfflineAccount(IAccountRepository repository)
{
    public async Task<Result<Account>> ExecuteAsync(string playerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string correlationId = Guid.NewGuid().ToString("N");
        Result<Account> created = new OfflineAccountFactory().Create(playerName, correlationId);
        if (!created.IsSuccess)
        {
            return created;
        }

        Account candidate = created.Value;
        Account? existingIdentity = await repository.FindByIdentityAsync(candidate.Identity, cancellationToken);
        if (existingIdentity is not null)
        {
            return Result<Account>.Success(existingIdentity);
        }

        IReadOnlyList<Account> existingAccounts = await repository.GetAllAsync(cancellationToken);
        if (existingAccounts.Any(account =>
                account.Type == AccountType.Offline &&
                string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)))
        {
            return Result<Account>.Failure(new Problem(
                "AUTH_OFFLINE_NAME_CASE_CONFLICT",
                ProblemStage.Authentication,
                "problem.auth.offline_name_case_conflict",
                false,
                correlationId,
                ["action.auth.use_existing_account"]));
        }

        Result<Domain.Common.Unit> saved = await repository.UpsertAsync(candidate, cancellationToken);
        return saved.IsSuccess
            ? Result<Account>.Success(candidate)
            : Result<Account>.Failure(saved.Problem!);
    }
}
