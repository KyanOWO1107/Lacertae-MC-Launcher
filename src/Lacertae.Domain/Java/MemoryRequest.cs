namespace Lacertae.Domain.Java;

public sealed record MemoryRequest(
    MemoryMode Mode,
    int? MinimumMb,
    int? MaximumMb,
    bool HasModLoader,
    int ModCount);
