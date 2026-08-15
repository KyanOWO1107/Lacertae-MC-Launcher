using Lacertae.Domain.Problems;

namespace Lacertae.Application.Accounts;

internal static class AccountProblem
{
    internal static Problem Required() => new(
        "AUTH_ACCOUNT_REQUIRED",
        ProblemStage.Authentication,
        "problem.auth.account_required",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.account.select"]);

    internal static Problem InvalidProfile() => new(
        "AUTH_PROFILE_UNAVAILABLE",
        ProblemStage.Authentication,
        "problem.auth.profile_unavailable",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.auth.retry"]);

    internal static Problem SecretFailure() => new(
        "AUTH_SESSION_EXPIRED",
        ProblemStage.Authentication,
        "problem.auth.session_expired",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.auth.sign_in_again"]);
}
