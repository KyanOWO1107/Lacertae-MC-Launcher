namespace Lacertae.Domain.Operations;

public sealed record OperationProgress(
    string Stage,
    long CompletedItems,
    long TotalItems,
    long CompletedBytes,
    long TotalBytes);
