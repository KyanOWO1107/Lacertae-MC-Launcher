namespace Lacertae.Domain.Install;

public sealed record InstallMove(
    string StagedRelativePath,
    string FinalRelativePath,
    string? QuarantineRelativePath,
    bool Applied);

public sealed record InstallJournal(
    string OperationId,
    string GameRootId,
    string VersionId,
    InstallJournalState State,
    IReadOnlyList<InstallMove> Moves,
    DateTimeOffset UpdatedUtc);
