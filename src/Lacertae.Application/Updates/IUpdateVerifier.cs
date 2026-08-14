using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Application.Updates;

public sealed record UpdateManifestEnvelope(
    UpdateManifest Manifest,
    byte[] Signature,
    byte[]? SourceBytes = null);

public sealed record VerifiedUpdateManifest(
    UpdateManifest Manifest,
    byte[] CanonicalBytes,
    byte[] Signature);

public interface IUpdateVerifier
{
    Result<VerifiedUpdateManifest> Verify(UpdateManifestEnvelope envelope);
}

public interface IUpdateManifestSource
{
    Task<Result<UpdateManifestEnvelope>> FetchAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken);
}

public enum UpdateCheckStatus
{
    Disabled,
    Checking,
    Current,
    Available,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    VerifiedUpdateManifest? Update,
    Problem? Problem);
