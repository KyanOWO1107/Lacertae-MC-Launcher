namespace Lacertae.Domain.Operations;

/// <summary>
/// The durable, secret-free state of a background operation.
/// </summary>
public sealed record BackgroundTaskRecord(
    string Id,
    string Kind,
    OperationState State,
    string FrozenPlanJson,
    string? JournalJson,
    string? ProblemCode,
    DateTimeOffset UpdatedUtc);
