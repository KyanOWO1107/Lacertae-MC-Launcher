using Lacertae.Application.Accounts;
using Lacertae.Application.Settings;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

namespace Lacertae.Application.Tests.Accounts;

public sealed class DeleteAccountTests
{
    [Fact]
    public async Task DeleteMarksAccountDeletingBeforeRemovingSecret()
    {
        Account account = AccountDeletionTestDoubles.MicrosoftAccount(AccountStatus.Active);
        AccountDeletionFakeRepository repository = new(account);
        AccountDeletionFakeVault vault = new();
        AccountDeletionFakeSettings settings = new(account.Id);

        Result<Unit> result = await new DeleteAccount(repository, vault, settings)
            .ExecuteAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["get", "status:Deleting", "delete-row"], repository.Events);
        Assert.Equal(["settings-load", "settings-save"], settings.Events);
        Assert.Equal(account.SecretRef, vault.DeletedReferences.Single());
        Assert.Null(settings.Stored.DefaultAccountId);
    }

    [Fact]
    public async Task DeleteOfflineAccountNeverCallsSecretVault()
    {
        Account account = AccountDeletionTestDoubles.OfflineAccount(AccountStatus.Active);
        AccountDeletionFakeRepository repository = new(account);
        AccountDeletionFakeVault vault = new() { ThrowIfCalled = true };

        Result<Unit> result = await new DeleteAccount(repository, vault, new AccountDeletionFakeSettings())
            .ExecuteAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Empty(vault.DeletedReferences);
        Assert.DoesNotContain("secret", repository.Events);
    }

    [Fact]
    public async Task DeleteKeepsDeletingRowWhenSecretRemovalFails()
    {
        Account account = AccountDeletionTestDoubles.MicrosoftAccount(AccountStatus.Active);
        AccountDeletionFakeRepository repository = new(account);
        AccountDeletionFakeVault vault = new() { DeleteResult = Result.Failure(AccountDeletionTestDoubles.Problem("SECRET_DECRYPT_FAILED")) };

        Result<Unit> result = await new DeleteAccount(repository, vault, new AccountDeletionFakeSettings())
            .ExecuteAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("SECRET_DECRYPT_FAILED", result.Problem?.Code);
        Assert.Equal(AccountStatus.Deleting, repository.Accounts.Values.Single().Status);
        Assert.DoesNotContain("delete-row", repository.Events);
    }
}

internal sealed class AccountDeletionFakeRepository : IAccountRepository
{
    public AccountDeletionFakeRepository(params Account[] accounts)
    {
        Accounts = accounts.ToDictionary(account => account.Id, StringComparer.Ordinal);
    }

    public Dictionary<string, Account> Accounts { get; }
    public List<string> Events { get; } = [];
    public Result<Unit> SetStatusResult { get; init; } = Result.Success();
    public Result<Unit> DeleteResult { get; init; } = Result.Success();

    public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken)
    {
        Events.Add("get-all");
        return Task.FromResult<IReadOnlyList<Account>>(Accounts.Values.ToArray());
    }

    public Task<Account?> GetAsync(string accountId, CancellationToken cancellationToken)
    {
        Events.Add("get");
        Accounts.TryGetValue(accountId, out Account? account);
        return Task.FromResult(account);
    }

    public Task<Account?> FindByIdentityAsync(AccountIdentity identity, CancellationToken cancellationToken) =>
        Task.FromResult(Accounts.Values.FirstOrDefault(account => account.Identity == identity));

    public Task<Result<Unit>> UpsertAsync(Account account, CancellationToken cancellationToken)
    {
        Accounts[account.Id] = account;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<Unit>> SetStatusAsync(string accountId, AccountStatus status, CancellationToken cancellationToken)
    {
        Events.Add("status:" + status);
        if (!SetStatusResult.IsSuccess)
        {
            return Task.FromResult(SetStatusResult);
        }

        Accounts[accountId] = Accounts[accountId] with { Status = status };
        return Task.FromResult(Result.Success());
    }

    public Task<Result<Unit>> DeleteAndClearVersionReferencesAsync(string accountId, CancellationToken cancellationToken)
    {
        Events.Add("delete-row");
        if (DeleteResult.IsSuccess)
        {
            Accounts.Remove(accountId);
        }

        return Task.FromResult(DeleteResult);
    }
}

internal sealed class AccountDeletionFakeVault : ISecretVault
{
    public List<string> DeletedReferences { get; } = [];
    public Result<Unit> DeleteResult { get; init; } = Result.Success();
    public bool ThrowIfCalled { get; init; }

    public Task<Result<Unit>> WriteAsync(string secretRef, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result<byte[]>> ReadAsync(string secretRef, CancellationToken cancellationToken) =>
        Task.FromResult(Result<byte[]>.Success([]));

    public Task<Result<Unit>> DeleteAsync(string secretRef, CancellationToken cancellationToken)
    {
        if (ThrowIfCalled)
        {
            throw new InvalidOperationException("Offline account attempted to use the secret vault.");
        }

        DeletedReferences.Add(secretRef);
        return Task.FromResult(DeleteResult);
    }
}

internal sealed class AccountDeletionFakeSettings(string? defaultAccountId = null) : ISettingsRepository
{
    public LauncherSettings Stored { get; private set; } = LauncherSettings.Default with { DefaultAccountId = defaultAccountId };
    public List<string> Events { get; } = [];
    public Result<LauncherSettings> LoadResult { get; init; } = Result<LauncherSettings>.Success(LauncherSettings.Default);
    public Result<Unit> SaveResult { get; init; } = Result.Success();

    public Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken)
    {
        Events.Add("settings-load");
        return Task.FromResult(LoadResult.IsSuccess
            ? Result<LauncherSettings>.Success(Stored)
            : LoadResult);
    }

    public Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        Events.Add("settings-save");
        if (SaveResult.IsSuccess)
        {
            Stored = settings;
        }

        return Task.FromResult(SaveResult);
    }
}

internal static class AccountDeletionTestDoubles
{
    public static Account MicrosoftAccount(AccountStatus status) => new(
        "microsoft-account-id-000000000000",
        new AccountIdentity(AccountIdentity.MicrosoftProviderId, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        AccountType.Microsoft,
        "Alex",
        "avatar-key",
        "0123456789abcdef0123456789abcdef",
        status,
        DateTimeOffset.UtcNow);

    public static Account OfflineAccount(AccountStatus status) => new(
        "offline-account-id-000000000000",
        new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
        AccountType.Offline,
        "Alex",
        null,
        null,
        status,
        null);

    public static Lacertae.Domain.Problems.Problem Problem(string code) => new(
        code,
        Lacertae.Domain.Problems.ProblemStage.Authentication,
        "problem.test.account_deletion",
        false,
        "account-deletion-test",
        []);
}
