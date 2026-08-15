using System.Globalization;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Accounts.Microsoft;

internal sealed record MicrosoftAuthOptions(string ClientId, string Authority, string RedirectUri)
{
    internal const string ConsumerAuthority = "https://login.microsoftonline.com/consumers";
    internal const string SystemBrowserRedirectUri = "http://localhost";

    internal static MicrosoftAuthOptions Create(string clientId, string? authority)
    {
        if (!Guid.TryParse(clientId, out Guid parsedClientId) || parsedClientId == Guid.Empty)
        {
            throw new ArgumentException("Client ID must be a non-empty GUID.", nameof(clientId));
        }

        string normalizedAuthority = NormalizeAuthority(authority);
        return new MicrosoftAuthOptions(
            parsedClientId.ToString("D", CultureInfo.InvariantCulture),
            normalizedAuthority,
            SystemBrowserRedirectUri);
    }

    internal static Result<MicrosoftAuthOptions?> TryCreate(string? clientId, string? authority)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Result<MicrosoftAuthOptions?>.Success(null);
        }

        try
        {
            return Result<MicrosoftAuthOptions?>.Success(Create(clientId, authority));
        }
        catch (ArgumentException)
        {
            return Result<MicrosoftAuthOptions?>.Failure(new Problem(
                "AUTH_MICROSOFT_CLIENT_ID_INVALID",
                ProblemStage.Configuration,
                "problem.auth.microsoft_client_id_invalid",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.auth.review"]));
        }
    }

    private static string NormalizeAuthority(string? authority)
    {
        string value = string.IsNullOrWhiteSpace(authority) ? ConsumerAuthority : authority;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/consumers", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort)
        {
            throw new ArgumentException("Authority must be the Microsoft consumers authority.", nameof(authority));
        }

        return ConsumerAuthority;
    }
}
