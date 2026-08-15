using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public sealed record AvatarCacheResult(
    string? CacheKey,
    bool UsesPlaceholder,
    DateTimeOffset CheckedUtc);

public interface IAvatarCache
{
    Task<Result<AvatarCacheResult>> RefreshAsync(Uri? skinUri, CancellationToken cancellationToken);

    string? ResolvePath(string? cacheKey);
}
