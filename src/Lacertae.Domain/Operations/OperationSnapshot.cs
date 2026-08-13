namespace Lacertae.Domain.Operations;

public sealed record OperationSnapshot(
    string Id,
    string Kind,
    OperationState State,
    OperationProgress? Progress,
    string? ProblemCode);
