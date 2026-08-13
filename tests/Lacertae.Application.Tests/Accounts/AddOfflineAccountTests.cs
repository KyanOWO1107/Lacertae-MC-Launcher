using Lacertae.Application.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Accounts;

public sealed class AddOfflineAccountTests
{
    [Fact]
    public async Task ExecuteAsyncPersistsNewAccountWithStableId()
    {
        FakeAccountRepository repository = new();
        AddOfflineAccount useCase = new(repository);

        Result<Account> result = await useCase.ExecuteAsync("Steve", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("offline", result.Value.Identity.ProviderId);
        Assert.Equal(32, result.Value.Id.Length);
        Assert.Equal(result.Value.Id, repository.Stored!.Id);
    }

    [Fact]
    public async Task ExecuteAsyncReturnsExistingExactIdentity()
    {
        Account existing = new(
            "account-1",
            new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
            AccountType.Offline,
            "Steve",
            null,
            null,
            AccountStatus.Active,
            null);
        FakeAccountRepository repository = new(existing);

        Result<Account> result = await new AddOfflineAccount(repository).ExecuteAsync(
            "Steve",
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Same(existing, result.Value);
        Assert.False(repository.UpsertCalled);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsCaseConflict()
    {
        string lowerCaseProfileUuid = new OfflineAccountFactory().Create("steve", "corr-1").Value.Identity.ProfileUuid;
        Account existing = new(
            "account-1",
            new AccountIdentity(AccountIdentity.OfflineProviderId, lowerCaseProfileUuid),
            AccountType.Offline,
            "steve",
            null,
            null,
            AccountStatus.Active,
            null);
        FakeAccountRepository repository = new(existing);

        Result<Account> result = await new AddOfflineAccount(repository).ExecuteAsync(
            "Steve",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_OFFLINE_NAME_CASE_CONFLICT", result.Problem?.Code);
    }

    private sealed class FakeAccountRepository(Account? existing = null) : IAccountRepository
    {
        public Account? Stored { get; private set; } = existing;
        public bool UpsertCalled { get; private set; }

        public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Account>>(Stored is null ? [] : [Stored]);

        public Task<Account?> GetAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.Id == accountId ? Stored : null);

        public Task<Account?> FindByIdentityAsync(AccountIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.Identity == identity ? Stored : null);

        public Task<Result<Unit>> UpsertAsync(Account account, CancellationToken cancellationToken)
        {
            UpsertCalled = true;
            Stored = account;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> SetStatusAsync(string accountId, AccountStatus status, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> DeleteAndClearVersionReferencesAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
