namespace Lacertae.Infrastructure.Accounts.Microsoft;

internal sealed record MicrosoftAuthBackendResult(
    string? PlayerName,
    string? ProfileUuid,
    string? AccessToken,
    string? UserType,
    string? Xuid,
    DateTimeOffset? ExpiresUtc,
    Uri? SkinUri,
    byte[]? SerializedCache,
    MicrosoftAuthFailure? Failure)
{
    internal bool IsSuccess => Failure is null;

    internal static MicrosoftAuthBackendResult Success(
        string playerName,
        string profileUuid,
        string accessToken,
        string userType,
        string? xuid,
        DateTimeOffset? expiresUtc,
        Uri? skinUri,
        byte[] serializedCache) => new(
            playerName,
            profileUuid,
            accessToken,
            userType,
            xuid,
            expiresUtc,
            skinUri,
            serializedCache?.ToArray() ?? throw new ArgumentNullException(nameof(serializedCache)),
            null);

    internal static MicrosoftAuthBackendResult FromFailure(MicrosoftAuthFailure failure) => new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        failure ?? throw new ArgumentNullException(nameof(failure)));

    public override string ToString() => IsSuccess
        ? $"MicrosoftAuthBackendResult({PlayerName}, {ProfileUuid}, [SECRET])"
        : $"MicrosoftAuthBackendResult(failure={Failure!.Kind})";
}
