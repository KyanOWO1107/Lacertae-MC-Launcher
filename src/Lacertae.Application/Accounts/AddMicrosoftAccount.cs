using System.Security.Cryptography;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed class AddMicrosoftAccount(
    IAccountRepository repository,
    ISecretVault secretVault,
    IMicrosoftIdentityClient identityClient,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Result<Account>> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result<MicrosoftLoginResult> login = await identityClient.SignInInteractivelyAsync(cancellationToken);
        if (!login.IsSuccess)
        {
            return Result<Account>.Failure(login.Problem!);
        }

        using MicrosoftLoginResult loginValue = login.Value;
        if (!IsValidProfile(loginValue))
        {
            return Result<Account>.Failure(AccountProblem.InvalidProfile());
        }

        string secretRef = CreateSecretReference();
        Result<Unit> secretWrite = await secretVault.WriteAsync(
            secretRef,
            loginValue.Cache.Bytes,
            cancellationToken);
        if (!secretWrite.IsSuccess)
        {
            return Result<Account>.Failure(secretWrite.Problem!);
        }

        Account? existing = await repository.FindByIdentityAsync(
            new AccountIdentity(AccountIdentity.MicrosoftProviderId, loginValue.ProfileUuid),
            cancellationToken);
        Account account = existing is null
            ? new Account(
                Guid.NewGuid().ToString("N"),
                new AccountIdentity(AccountIdentity.MicrosoftProviderId, loginValue.ProfileUuid),
                AccountType.Microsoft,
                loginValue.PlayerName,
                existing?.AvatarCacheKey,
                secretRef,
                AccountStatus.Active,
                timeProvider.GetUtcNow())
            : existing with
            {
                Identity = new AccountIdentity(AccountIdentity.MicrosoftProviderId, loginValue.ProfileUuid),
                Type = AccountType.Microsoft,
                PlayerName = loginValue.PlayerName,
                SecretRef = secretRef,
                Status = AccountStatus.Active,
                LastSuccessfulLoginUtc = timeProvider.GetUtcNow(),
            };

        Result<Unit> saved = await repository.UpsertAsync(account, cancellationToken);
        if (!saved.IsSuccess)
        {
            await secretVault.DeleteAsync(secretRef, cancellationToken);
            return Result<Account>.Failure(saved.Problem!);
        }

        if (existing?.SecretRef is string oldSecretRef &&
            !string.Equals(oldSecretRef, secretRef, StringComparison.Ordinal))
        {
            Result<Unit> oldSecretDelete = await secretVault.DeleteAsync(oldSecretRef, cancellationToken);
            if (!oldSecretDelete.IsSuccess)
            {
                return Result<Account>.Failure(oldSecretDelete.Problem!);
            }
        }

        return Result<Account>.Success(account);
    }

    private static bool IsValidProfile(MicrosoftLoginResult login) =>
        Guid.TryParse(login.ProfileUuid, out Guid profileUuid) &&
        profileUuid != Guid.Empty &&
        string.Equals(profileUuid.ToString("D"), login.ProfileUuid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(login.Session.ProfileUuid, login.ProfileUuid, StringComparison.Ordinal) &&
        string.Equals(login.Session.PlayerName, login.PlayerName, StringComparison.Ordinal);

    private static string CreateSecretReference() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
