using System.Security.Cryptography;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Lacertae.Domain.Accounts;
using Microsoft.Identity.Client;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.Game.XboxAuth;
using XboxAuthNet.OAuth;
using XboxAuthNet.XboxLive;

namespace Lacertae.Infrastructure.Accounts.Microsoft;

internal sealed class CmlLibMicrosoftAuthBackend : IMicrosoftAuthBackend
{
    private const string RelyingParty = "rp://api.minecraftservices.com/";

    public async Task<MicrosoftAuthBackendResult> SignInInteractivelyAsync(
        MicrosoftAuthOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(options, ReadOnlyMemory<byte>.Empty, interactive: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return MicrosoftAuthBackendResult.FromFailure(MapException(exception, interactive: true));
        }
    }

    public async Task<MicrosoftAuthBackendResult> RefreshSilentlyAsync(
        MicrosoftAuthOptions options,
        ReadOnlyMemory<byte> serializedCache,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(options, serializedCache, interactive: false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return MicrosoftAuthBackendResult.FromFailure(MapException(exception, interactive: false));
        }
    }

    internal static IPublicClientApplication BuildMsalApplication(MicrosoftAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PublicClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(options.Authority)
            .WithRedirectUri(options.RedirectUri)
            .Build();
    }

    internal static CmlLib.Core.Auth.Microsoft.Authenticators.JEAuthenticatorBuilder CreateOwnershipBuilder() =>
        new CmlLib.Core.Auth.Microsoft.Authenticators.JEAuthenticatorBuilder().WithGameOwnershipChecker();

    private static async Task<MicrosoftAuthBackendResult> ExecuteAsync(
        MicrosoftAuthOptions options,
        ReadOnlyMemory<byte> serializedCache,
        bool interactive,
        CancellationToken cancellationToken)
    {
        using MsalCacheBridge cacheBridge = new(serializedCache);
        IPublicClientApplication application = BuildMsalApplication(options);
        cacheBridge.Attach(application.UserTokenCache);
        using HttpClient httpClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        InMemoryXboxGameAccountManager accountManager = new(
            storage => new JEGameAccount(storage));
        XboxGameLoginHandlerBuilder baseBuilder = new()
        {
            HttpClient = httpClient,
            AccountManager = accountManager,
        };
        LoginHandlerParameters parameters = baseBuilder.BuildParameters();
        MsalCodeFlowProvider oauthProvider = new(application);
        BasicXboxProvider xboxProvider = new(RelyingParty);
        JELoginHandler loginHandler = new(parameters, oauthProvider, xboxProvider);
        // A fresh in-memory account is used for both flows. The durable MSAL
        // cache is the only input to silent refresh; CmlLib never writes its
        // JSON account manager in this adapter.
        IXboxGameAccount account = accountManager.NewAccount();
        NestedAuthenticator authenticator = loginHandler.CreateAuthenticator(account, cancellationToken);

        if (interactive)
        {
            authenticator.AddAuthenticatorWithoutValidator(oauthProvider.AuthenticateInteractively());
            authenticator.AddAuthenticatorWithoutValidator(xboxProvider.AuthenticateInteractively());
        }
        else
        {
            authenticator.AddAuthenticatorWithoutValidator(oauthProvider.AuthenticateSilently());
            authenticator.AddAuthenticatorWithoutValidator(xboxProvider.AuthenticateSilently());
        }

        authenticator.AddForceJEAuthenticator(builder => builder.WithGameOwnershipChecker().Build());
        MSession session = await authenticator.ExecuteForLauncherAsync();
        JEGameAccount gameAccount = JEGameAccount.FromSessionStorage(account.SessionStorage);
        Uri? skinUri = gameAccount.Profile?.Skins?
            .FirstOrDefault(static skin => string.Equals(skin.Variant, "CLASSIC", StringComparison.OrdinalIgnoreCase))?.Url is string skin
            ? new Uri(skin, UriKind.Absolute)
            : null;
        byte[]? cache = cacheBridge.TakeLatestCache();
        if (cache is null)
        {
            return MicrosoftAuthBackendResult.FromFailure(
                new MicrosoftAuthFailure(MicrosoftAuthFailureKind.ProfileUnavailable));
        }

        if (string.IsNullOrWhiteSpace(session.Username) ||
            string.IsNullOrWhiteSpace(session.UUID) ||
            string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return MicrosoftAuthBackendResult.FromFailure(
                new MicrosoftAuthFailure(MicrosoftAuthFailureKind.ProfileUnavailable));
        }

        string playerName = session.Username;
        string profileUuid = session.UUID;
        string accessToken = session.AccessToken;
        string userType = string.IsNullOrWhiteSpace(session.UserType) ? "msa" : session.UserType;
        string? xuid = session.Xuid;
        MicrosoftAuthBackendResult mapped;
        try
        {
            mapped = MicrosoftAuthBackendResult.Success(
                playerName,
                profileUuid,
                accessToken,
                userType,
                xuid,
                null,
                skinUri,
                cache);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cache);
            session.AccessToken = string.Empty;
            if (gameAccount.Token is not null)
            {
                gameAccount.Token.AccessToken = string.Empty;
            }

            accountManager.ClearAccounts();
        }

        return mapped;
    }

    private static MicrosoftAuthFailure MapException(Exception exception, bool interactive)
    {
        if (exception is OperationCanceledException)
        {
            return new MicrosoftAuthFailure(MicrosoftAuthFailureKind.Cancelled);
        }

        if (!interactive && exception is MsalUiRequiredException)
        {
            return new MicrosoftAuthFailure(MicrosoftAuthFailureKind.SessionExpired);
        }

        if (exception is MicrosoftOAuthException oauthException)
        {
            return new MicrosoftAuthFailure(
                oauthException.StatusCode is 400 or 401
                    ? MicrosoftAuthFailureKind.StateInvalid
                    : MicrosoftAuthFailureKind.NetworkFailed,
                NormalizeStatus(oauthException.StatusCode));
        }

        if (exception is XboxAuthException xboxException)
        {
            return new MicrosoftAuthFailure(
                xboxException.StatusCode is 401 or 403
                    ? MicrosoftAuthFailureKind.XstsRejected
                    : MicrosoftAuthFailureKind.NetworkFailed,
                NormalizeStatus(xboxException.StatusCode));
        }

        if (exception is JEAuthException jeException)
        {
            return new MicrosoftAuthFailure(
                jeException.StatusCode is 401 or 403
                    ? MicrosoftAuthFailureKind.OwnershipRequired
                    : MicrosoftAuthFailureKind.ProfileUnavailable,
                NormalizeStatus(jeException.StatusCode));
        }

        if (exception is MsalServiceException serviceException)
        {
            return new MicrosoftAuthFailure(
                serviceException.StatusCode is >= 500
                    ? MicrosoftAuthFailureKind.NetworkFailed
                    : MicrosoftAuthFailureKind.ProfileUnavailable,
                NormalizeStatus(serviceException.StatusCode));
        }

        if (exception is MsalClientException clientException &&
            string.Equals(clientException.ErrorCode, "authentication_canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new MicrosoftAuthFailure(MicrosoftAuthFailureKind.Cancelled);
        }

        return new MicrosoftAuthFailure(MicrosoftAuthFailureKind.NetworkFailed);
    }

    private static int? NormalizeStatus(int statusCode) =>
        statusCode is >= 100 and <= 599 ? statusCode : null;
}
