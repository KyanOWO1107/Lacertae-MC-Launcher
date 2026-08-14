namespace Lacertae.Application.Diagnostics;

/// <summary>
/// Supplies the small, explicit set of diagnostic candidates that may be
/// previewed. Implementations must not return databases, secrets, OAuth
/// configuration, process environments or unselected game logs.
/// </summary>
public interface IDiagnosticSource
{
    /// <summary>
    /// Collects candidates for one request. The default implementation keeps
    /// the interface source-compatible with simple test sources that override
    /// <see cref="GetEntriesAsync"/> instead.
    /// </summary>
    Task<IReadOnlyList<DiagnosticSourceEntry>> CollectAsync(
        DiagnosticBundleRequest request,
        CancellationToken cancellationToken) => GetEntriesAsync(request, cancellationToken);

    /// <summary>
    /// Alias retained for adapters whose vocabulary is “get entries”.
    /// </summary>
    Task<IReadOnlyList<DiagnosticSourceEntry>> GetEntriesAsync(
        DiagnosticBundleRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiagnosticSourceEntry>>([]);
}
