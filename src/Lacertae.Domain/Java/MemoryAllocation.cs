namespace Lacertae.Domain.Java;

public sealed record MemoryAllocation(
    int MinimumMb,
    int MaximumMb,
    MemoryMode Mode);
