namespace Lacertae.Domain.Versions;

public sealed record VersionCharacteristics(
    bool HasModLoader,
    string? VersionType);
