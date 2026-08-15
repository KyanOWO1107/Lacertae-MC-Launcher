using Lacertae.Domain.Problems;

namespace Lacertae.Infrastructure.Accounts.Microsoft;

internal static class MicrosoftAuthProblemMapper
{
    internal static Problem Map(MicrosoftAuthFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        (string code, bool retryable, string action) = failure.Kind switch
        {
            MicrosoftAuthFailureKind.Cancelled => ("AUTH_CANCELLED", false, "action.auth.sign_in_again"),
            MicrosoftAuthFailureKind.StateInvalid => ("AUTH_STATE_INVALID", false, "action.auth.sign_in_again"),
            MicrosoftAuthFailureKind.XstsRejected => ("AUTH_XSTS_REJECTED", false, "action.auth.review_microsoft_account"),
            MicrosoftAuthFailureKind.OwnershipRequired => ("AUTH_OWNERSHIP_REQUIRED", false, "action.auth.verify_ownership"),
            MicrosoftAuthFailureKind.ProfileUnavailable => ("AUTH_PROFILE_UNAVAILABLE", IsServerFailure(failure), "action.auth.retry"),
            MicrosoftAuthFailureKind.SessionExpired => ("AUTH_SESSION_EXPIRED", false, "action.auth.sign_in_again"),
            MicrosoftAuthFailureKind.NetworkFailed => ("AUTH_NETWORK_FAILED", true, "action.auth.retry"),
            MicrosoftAuthFailureKind.Configuration => ("AUTH_MICROSOFT_NOT_CONFIGURED", false, "action.auth.review"),
            _ => ("AUTH_NETWORK_FAILED", true, "action.auth.retry"),
        };

        Dictionary<string, string> safeContext = new(StringComparer.Ordinal);
        if (failure.HttpStatusCode is int statusCode && statusCode is >= 100 and <= 599)
        {
            safeContext["httpStatus"] = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new Problem(
            code,
            ProblemStage.Authentication,
            "problem.auth.microsoft_failed",
            retryable,
            Guid.NewGuid().ToString("N"),
            [action],
            safeContext);
    }

    private static bool IsServerFailure(MicrosoftAuthFailure failure) =>
        failure.HttpStatusCode is >= 500 and <= 599;
}
