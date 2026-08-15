using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lacertae.Application.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Accounts.Microsoft;

public sealed class CmlLibMicrosoftIdentityClient : IMicrosoftIdentityClient
{
    private static readonly Regex JavaName = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly Result<MicrosoftAuthOptions?> options;
    private readonly IMicrosoftAuthBackend backend;

    public CmlLibMicrosoftIdentityClient(string? clientId, string? authority = null)
    {
        options = MicrosoftAuthOptions.TryCreate(clientId, authority);
        backend = new CmlLibMicrosoftAuthBackend();
    }

    internal CmlLibMicrosoftIdentityClient(MicrosoftAuthOptions? options, IMicrosoftAuthBackend backend)
    {
        this.options = Result<MicrosoftAuthOptions?>.Success(options);
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public Task<Result<MicrosoftLoginResult>> SignInInteractivelyAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(static (backend, options, token) => backend.SignInInteractivelyAsync(options, token), cancellationToken);

    public Task<Result<MicrosoftLoginResult>> RefreshSilentlyAsync(
        ReadOnlyMemory<byte> serializedCache,
        CancellationToken cancellationToken)
    {
        if (serializedCache.IsEmpty)
        {
            return Task.FromResult(Result<MicrosoftLoginResult>.Failure(new Problem(
                "AUTH_SESSION_EXPIRED",
                ProblemStage.Authentication,
                "problem.auth.session_expired",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.auth.sign_in_again"])));
        }

        return ExecuteAsync(
            (authBackend, authOptions, token) => authBackend.RefreshSilentlyAsync(authOptions, serializedCache, token),
            cancellationToken);
    }

    private async Task<Result<MicrosoftLoginResult>> ExecuteAsync(
        Func<IMicrosoftAuthBackend, MicrosoftAuthOptions, CancellationToken, Task<MicrosoftAuthBackendResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.IsSuccess)
        {
            return Result<MicrosoftLoginResult>.Failure(options.Problem!);
        }

        if (options.Value is null)
        {
            return Result<MicrosoftLoginResult>.Failure(MicrosoftAuthProblemMapper.Map(
                new MicrosoftAuthFailure(MicrosoftAuthFailureKind.Configuration)));
        }

        MicrosoftAuthBackendResult result = await operation(backend, options.Value, cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<MicrosoftLoginResult>.Failure(MicrosoftAuthProblemMapper.Map(result.Failure!));
        }

        return MapSuccess(result);
    }

    private static Result<MicrosoftLoginResult> MapSuccess(MicrosoftAuthBackendResult result)
    {
        if (string.IsNullOrWhiteSpace(result.PlayerName) || !JavaName.IsMatch(result.PlayerName) ||
            !Guid.TryParse(result.ProfileUuid, out Guid profileUuid) ||
            string.IsNullOrWhiteSpace(result.AccessToken) ||
            string.IsNullOrWhiteSpace(result.UserType) ||
            result.SerializedCache is not { Length: > 0 })
        {
            return Result<MicrosoftLoginResult>.Failure(new Problem(
                "AUTH_PROFILE_UNAVAILABLE",
                ProblemStage.Authentication,
                "problem.auth.microsoft_failed",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.auth.retry"]));
        }

        Uri? skinUri = IsSafeSkinUri(result.SkinUri) ? result.SkinUri : null;
        AuthSession session = new(
            result.PlayerName,
            profileUuid.ToString("D").ToLowerInvariant(),
            new SensitiveString(result.AccessToken),
            result.UserType,
            result.Xuid,
            result.ExpiresUtc);
        try
        {
            MicrosoftLoginResult login = new(
                result.PlayerName,
                profileUuid.ToString("D").ToLowerInvariant(),
                session,
                skinUri,
                new SecretMaterial(result.SerializedCache));
            return Result<MicrosoftLoginResult>.Success(login);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(result.SerializedCache);
        }
    }

    private static bool IsSafeSkinUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal) &&
        uri.AbsolutePath.Length > "/texture/".Length &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.IsNullOrEmpty(uri.UserInfo);
}
