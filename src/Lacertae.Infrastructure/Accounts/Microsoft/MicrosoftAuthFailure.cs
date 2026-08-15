namespace Lacertae.Infrastructure.Accounts.Microsoft;

internal enum MicrosoftAuthFailureKind
{
    Cancelled,
    StateInvalid,
    XstsRejected,
    OwnershipRequired,
    ProfileUnavailable,
    SessionExpired,
    NetworkFailed,
    Configuration,
}

internal sealed record MicrosoftAuthFailure(
    MicrosoftAuthFailureKind Kind,
    int? HttpStatusCode = null,
    string? Classification = null);
