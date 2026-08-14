using System.Text.Json.Serialization;

namespace Lacertae.Domain.Accounts;

public sealed class AuthSession
{
    public AuthSession(
        string playerName,
        string profileUuid,
        SensitiveString accessToken,
        string userType,
        string? xuid,
        DateTimeOffset? expiresUtc)
    {
        PlayerName = Require(playerName, nameof(playerName));
        ProfileUuid = Require(profileUuid, nameof(profileUuid));
        AccessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        UserType = Require(userType, nameof(userType));
        Xuid = string.IsNullOrWhiteSpace(xuid) ? null : xuid;
        ExpiresUtc = expiresUtc;
    }

    public string PlayerName { get; }

    public string ProfileUuid { get; }

    [JsonIgnore]
    public SensitiveString AccessToken { get; }

    public string UserType { get; }

    public string? Xuid { get; }

    public DateTimeOffset? ExpiresUtc { get; }

    public override string ToString() => $"AuthSession({PlayerName}, {ProfileUuid}, [SECRET])";

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be blank.", parameterName)
            : value;
}
