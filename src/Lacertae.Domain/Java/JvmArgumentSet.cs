namespace Lacertae.Domain.Java;

public sealed record JvmArgumentSet(
    IReadOnlyList<string> MemoryArguments,
    IReadOnlyList<string> GarbageCollectorArguments,
    IReadOnlyList<string> UserArguments)
{
    public IReadOnlyList<string> Flatten() =>
        [.. MemoryArguments, .. GarbageCollectorArguments, .. UserArguments];
}
