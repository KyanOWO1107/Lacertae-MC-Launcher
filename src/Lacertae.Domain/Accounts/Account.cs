namespace Lacertae.Domain.Accounts;

public sealed record Account(
    string Id,
    AccountIdentity Identity,
    AccountType Type,
    string PlayerName,
    string? AvatarCacheKey,
    string? SecretRef,
    AccountStatus Status,
    DateTimeOffset? LastSuccessfulLoginUtc);
