namespace Lacertae.Domain.Launch;

public sealed record GameExitResult(
    int? ProcessId,
    int? ExitCode,
    GameProcessState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string CorrelationId);
