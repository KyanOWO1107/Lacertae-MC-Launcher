namespace Lacertae.Domain.GameRoots;

public sealed record GameRoot(
    string Id,
    string NormalizedPath,
    string DisplayName,
    GameRootAvailability Availability,
    DateTimeOffset? LastScannedUtc);
