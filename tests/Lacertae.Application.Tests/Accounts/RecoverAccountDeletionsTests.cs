using Lacertae.Application.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Accounts;

public sealed class RecoverAccountDeletionsTests
{
    [Fact]
    public async Task RecoverRetriesIdempotentlyAfterSecretWasAlreadyRemoved()
    {
        Account account = AccountDeletionTestDoubles.MicrosoftAccount(AccountStatus.Deleting);
        AccountDeletionFakeRepository repository = new(account);
        AccountDeletionFakeVault vault = new();
        DeleteAccount deletion = new(repository, vault, new AccountDeletionFakeSettings());
        RecoverAccountDeletions recovery = new(repository, deletion);

        Result<Unit> first = await recovery.ExecuteAsync(TestContext.Current.CancellationToken);
        Result<Unit> second = await recovery.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Problem?.Code);
        Assert.True(second.IsSuccess, second.Problem?.Code);
        Assert.Empty(repository.Accounts);
        Assert.Single(vault.DeletedReferences);
    }

    [Fact]
    public async Task RecoverPreservesDeletingRowWhenVaultReturnsNonRetryableFailure()
    {
        Account account = AccountDeletionTestDoubles.MicrosoftAccount(AccountStatus.Deleting);
        AccountDeletionFakeRepository repository = new(account);
        AccountDeletionFakeVault vault = new()
        {
            DeleteResult = Result.Failure(AccountDeletionTestDoubles.Problem("SECRET_DECRYPT_FAILED")),
        };
        RecoverAccountDeletions recovery = new(
            repository,
            new DeleteAccount(repository, vault, new AccountDeletionFakeSettings()));

        Result<Unit> result = await recovery.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("SECRET_DECRYPT_FAILED", result.Problem?.Code);
        Assert.Equal(AccountStatus.Deleting, repository.Accounts.Values.Single().Status);
    }
}
