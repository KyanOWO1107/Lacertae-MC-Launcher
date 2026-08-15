namespace Lacertae.Domain.Updates;

/// <summary>
/// A narrow, path-explicit contract passed to the standalone updater. All
/// files are relative to one of the roots in this record; the updater never
/// accepts a command line or wildcard as an update instruction.
/// </summary>
public sealed record UpdateApplyPlan(
    int ParentProcessId,
    string ParentExecutablePath,
    string InstallDirectory,
    string StagingDirectory,
    string BackupDirectory,
    string NewExecutableRelativePath,
    string HealthFilePath,
    string HealthNonce,
    TimeSpan HealthTimeout,
    IReadOnlyList<string> OldManifestFiles,
    IReadOnlyList<string> NewManifestFiles);
