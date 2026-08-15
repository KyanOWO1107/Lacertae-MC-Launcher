using Lacertae.Domain.Accounts;

namespace Lacertae.Application.Accounts;

/// <summary>
/// Public profile data plus the short-lived session and renewable cache returned by Microsoft login.
/// </summary>
public sealed class MicrosoftLoginResult : IDisposable
{
    public MicrosoftLoginResult(
        string playerName,
        string profileUuid,
        AuthSession session,
        Uri? skinUri,
        SecretMaterial cache)
    {
        PlayerName = string.IsNullOrWhiteSpace(playerName)
            ? throw new ArgumentException("Player name cannot be blank.", nameof(playerName))
            : playerName;
        ProfileUuid = string.IsNullOrWhiteSpace(profileUuid)
            ? throw new ArgumentException("Profile UUID cannot be blank.", nameof(profileUuid))
            : profileUuid;
        Session = session ?? throw new ArgumentNullException(nameof(session));
        SkinUri = skinUri;
        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string PlayerName { get; }

    public string ProfileUuid { get; }

    public AuthSession Session { get; }

    public Uri? SkinUri { get; }

    public SecretMaterial Cache { get; }

    public void Dispose() => Cache.Dispose();

    public override string ToString() =>
        $"MicrosoftLoginResult({PlayerName}, {ProfileUuid}, [SECRET])";
}
