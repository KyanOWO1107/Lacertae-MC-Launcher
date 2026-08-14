namespace Lacertae.Domain.Diagnostics;

/// <summary>
/// Describes one logical item in a locally prepared diagnostic bundle.
/// Logical names are relative ZIP names and never contain source paths.
/// </summary>
public sealed record DiagnosticBundleEntry(
    string LogicalName,
    long Size,
    string Sha256,
    bool IsIncluded,
    string RedactionSummary);

/// <summary>
/// The public, path-free preview manifest for a diagnostic bundle.
/// </summary>
public sealed record DiagnosticBundleManifest(
    int SchemaVersion,
    string LauncherVersion,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<DiagnosticBundleEntry> Entries);

/// <summary>
/// Opaque identifier for a prepared bundle. It deliberately carries no
/// filesystem path; only the writer that owns the staging root can resolve it.
/// </summary>
public sealed record DiagnosticBundlePreparedHandle(string Id);
