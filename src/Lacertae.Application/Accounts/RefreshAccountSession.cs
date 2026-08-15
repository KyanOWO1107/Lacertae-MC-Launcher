using System.Security.Cryptography;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class RefreshAccountSession(
    IAccountRepository repository,
    ISecretVault secretVault,
    IMicrosoftIdentityClient identityClient,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Result<AuthSession>> ExecuteAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        Account? account = await repository.GetAsync(accountId, cancellationToken);
        if (account is null || account.Status == AccountStatus.Deleting)
        {
            return Result<AuthSession>.Failure(AccountProblem.Required());
        }

        if (account.Type == AccountType.Offline)
        {
            return Result<AuthSession>.Success(new AuthSession(
                account.PlayerName,
                account.Identity.ProfileUuid,
                new SensitiveString("0"),
                "legacy",
                null,
                null));
        }

        if (string.IsNullOrWhiteSpace(account.SecretRef))
        {
            await MarkReauthenticationRequiredAsync(account, cancellationToken);
            return Result<AuthSession>.Failure(AccountProblem.SecretFailure());
        }

        Result<byte[]> cache = await secretVault.ReadAsync(account.SecretRef, cancellationToken);
        if (!cache.IsSuccess || cache.Value.Length == 0)
        {
            await MarkReauthenticationRequiredAsync(account, cancellationToken);
            return Result<AuthSession>.Failure(AccountProblem.SecretFailure());
        }

        Result<MicrosoftLoginResult> refreshed;
        try
        {
            refreshed = await identityClient.RefreshSilentlyAsync(cache.Value, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cache.Value);
        }

        if (!refreshed.IsSuccess)
        {
            if (refreshed.Problem?.Code == "AUTH_SESSION_EXPIRED")
            {
                await MarkReauthenticationRequiredAsync(account, cancellationToken);
            }

            return Result<AuthSession>.Failure(refreshed.Problem!);
        }

        using MicrosoftLoginResult login = refreshed.Value;
        if (!string.Equals(login.ProfileUuid, account.Identity.ProfileUuid, StringComparison.OrdinalIgnoreCase))
        {
            return Result<AuthSession>.Failure(AccountProblem.InvalidProfile());
        }

        Result<Unit> cacheWrite = await secretVault.WriteAsync(
            account.SecretRef,
            login.Cache.Bytes,
            cancellationToken);
        if (!cacheWrite.IsSuccess)
        {
            return Result<AuthSession>.Failure(cacheWrite.Problem!);
        }

        Account updated = account with
        {
            PlayerName = login.PlayerName,
            Status = AccountStatus.Active,
            LastSuccessfulLoginUtc = timeProvider.GetUtcNow(),
        };
        Result<Unit> saved = await repository.UpsertAsync(updated, cancellationToken);
        return saved.IsSuccess
            ? Result<AuthSession>.Success(login.Session)
            : Result<AuthSession>.Failure(saved.Problem!);
    }

    private async Task MarkReauthenticationRequiredAsync(Account account, CancellationToken cancellationToken)
    {
        if (account.Status != AccountStatus.ReauthenticationRequired)
        {
            await repository.SetStatusAsync(account.Id, AccountStatus.ReauthenticationRequired, cancellationToken);
        }
    }
}
