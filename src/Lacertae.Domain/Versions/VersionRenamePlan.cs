namespace Lacertae.Domain.Versions;

public enum VersionRenameJournalState
{
    Prepared,
    DirectoryMoved,
    DatabaseUpdated,
    Completed,
    RollbackRequired,
}

public sealed record VersionRenamePlan(
    string OperationId,
    string GameRootId,
    string SourceFolder,
    string TargetFolder,
    string SourcePath,
    string TargetPath,
    string SourceJsonPath,
    string TargetJsonPath,
    string? SourceJarPath,
    string? TargetJarPath,
    bool ContainsIsolatedGameData);

public sealed record VersionRenameJournalEntry(
    VersionRenamePlan Plan,
    VersionRenameJournalState State);
