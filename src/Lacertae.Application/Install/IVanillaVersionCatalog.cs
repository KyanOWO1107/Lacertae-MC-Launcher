using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Install;

/// <summary>
/// The validated official vanilla version index used by the desktop download page.
/// </summary>
public sealed record VanillaVersionSummary(
    string Id,
    string Type,
    DateTimeOffset ReleaseTime,
    Uri MetadataUri,
    string MetadataSha1);

public interface IVanillaVersionCatalog
{
    Task<Result<IReadOnlyList<VanillaVersionSummary>>> ListAsync(
        CancellationToken cancellationToken);
}
