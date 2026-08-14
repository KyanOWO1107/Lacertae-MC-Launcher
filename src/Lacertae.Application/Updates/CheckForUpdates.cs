using System.Security.Cryptography;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Application.Updates;

/// <summary>
/// Performs a non-blocking update check. A transport, parse or trust error is
/// represented as a retryable status and never becomes a startup failure.
/// </summary>
public sealed class CheckForUpdates
{
    private readonly IUpdateManifestSource source;
    private readonly IUpdateVerifier verifier;
    private readonly UpdateChannel channel;
    private readonly bool enabled;

    public CheckForUpdates(
        IUpdateManifestSource source,
        IUpdateVerifier verifier,
        UpdateChannel channel = UpdateChannel.Stable,
        bool enabled = true)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        this.channel = channel;
        this.enabled = enabled;
    }

    public async Task<Result<UpdateCheckResult>> ExecuteAsync(
        string currentLauncherVersion,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return Result<UpdateCheckResult>.Success(new UpdateCheckResult(
                UpdateCheckStatus.Disabled,
                null,
                null));
        }

        if (!UpdateManifest.IsValidSemanticVersion(currentLauncherVersion))
        {
            return Result<UpdateCheckResult>.Success(Failed(Problem(
                "UPDATE_CURRENT_VERSION_INVALID",
                isRetryable: false)));
        }

        Result<UpdateManifestEnvelope> fetched;
        try
        {
            fetched = await source.FetchAsync(channel, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException)
        {
            return Result<UpdateCheckResult>.Success(Failed(Problem(
                "UPDATE_CHECK_FAILED",
                isRetryable: true)));
        }

        if (!fetched.IsSuccess)
        {
            return Result<UpdateCheckResult>.Success(Failed(fetched.Problem ?? Problem(
                "UPDATE_CHECK_FAILED",
                isRetryable: true)));
        }

        if (fetched.Value.Manifest.Channel != channel)
        {
            return Result<UpdateCheckResult>.Success(Failed(Problem(
                "UPDATE_CHANNEL_MISMATCH",
                isRetryable: false)));
        }

        Result<VerifiedUpdateManifest> verified;
        try
        {
            verified = verifier.Verify(fetched.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or InvalidOperationException)
        {
            return Result<UpdateCheckResult>.Success(Failed(Problem(
                "UPDATE_SIGNATURE_INVALID",
                isRetryable: false)));
        }

        if (!verified.IsSuccess)
        {
            return Result<UpdateCheckResult>.Success(Failed(verified.Problem ?? Problem(
                "UPDATE_SIGNATURE_INVALID",
                isRetryable: false)));
        }

        int comparison = UpdateManifest.CompareSemanticVersions(
            verified.Value.Manifest.Version,
            currentLauncherVersion);
        return Result<UpdateCheckResult>.Success(comparison > 0
            ? new UpdateCheckResult(UpdateCheckStatus.Available, verified.Value, null)
            : new UpdateCheckResult(UpdateCheckStatus.Current, verified.Value, null));
    }

    private static UpdateCheckResult Failed(Problem problem) => new(
        UpdateCheckStatus.Failed,
        null,
        problem);

    private static Problem Problem(string code, bool isRetryable) => new(
        code,
        ProblemStage.Update,
        "problem.update.check_failed",
        isRetryable,
        Guid.NewGuid().ToString("N"),
        ["action.update.retry"]);
}
