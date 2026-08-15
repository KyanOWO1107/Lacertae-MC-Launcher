using Lacertae.Application.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Infrastructure.Accounts.Microsoft;
using Microsoft.Identity.Client;

namespace Lacertae.Infrastructure.Tests.Accounts.Microsoft;

public sealed class CmlLibMicrosoftIdentityClientTests
{
    [Fact]
    public async Task MissingClientIdReturnsNotConfiguredWithoutCallingBackend()
    {
        RecordingBackend backend = new();
        CmlLibMicrosoftIdentityClient client = new(null, backend);

        var result = await client.SignInInteractivelyAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_MICROSOFT_NOT_CONFIGURED", result.Problem?.Code);
        Assert.Equal(0, backend.InteractiveCalls);
    }

    [Fact]
    public void ProductionOptionsPinConsumerAuthorityAndLoopbackRedirect()
    {
        MicrosoftAuthOptions options = MicrosoftAuthOptions.Create(
            "11111111-1111-1111-1111-111111111111",
            "https://login.microsoftonline.com/consumers/");

        Assert.Equal("11111111-1111-1111-1111-111111111111", options.ClientId);
        Assert.Equal("https://login.microsoftonline.com/consumers", options.Authority);
        Assert.Equal("http://localhost", options.RedirectUri);
    }

    [Fact]
    public void MsalApplicationUsesOnlyPublicClientAndExactLoopbackRedirect()
    {
        MicrosoftAuthOptions options = MicrosoftAuthOptions.Create(
            "11111111-1111-1111-1111-111111111111",
            null);

        IPublicClientApplication application = CmlLibMicrosoftAuthBackend.BuildMsalApplication(options);

        Assert.Equal(options.ClientId, application.AppConfig.ClientId);
        Assert.Equal(options.Authority, application.Authority.TrimEnd('/'));
        Assert.Equal("http://localhost", application.AppConfig.RedirectUri);
        Assert.True(string.IsNullOrEmpty(application.AppConfig.ClientSecret));
    }

    [Fact]
    public void CmlLibPipelineEnablesJavaEditionOwnershipCheck()
    {
        var builder = CmlLibMicrosoftAuthBackend.CreateOwnershipBuilder();

        Assert.True(builder.CheckGameOwnership);
    }

    [Fact]
    public async Task CancelledInteractiveRequestBuildsPipelineWithoutNetworkAccess()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        MicrosoftAuthOptions options = MicrosoftAuthOptions.Create(
            "11111111-1111-1111-1111-111111111111",
            "https://login.microsoftonline.com/consumers");
        MicrosoftAuthBackendResult result = await new CmlLibMicrosoftAuthBackend()
            .SignInInteractivelyAsync(options, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MicrosoftAuthFailureKind.Cancelled, result.Failure?.Kind);
    }

    [Fact]
    public async Task SuccessfulBackendResultMapsToLacertaeOnlySessionAndRedactsSecrets()
    {
        byte[] cache = [1, 2, 3, 4];
        RecordingBackend backend = new(MicrosoftAuthBackendResult.Success(
            "Steve",
            "5627DD98-E6BE-3C21-B8A8-E92344183641",
            "access-token",
            "msa",
            "123456789",
            DateTimeOffset.UtcNow.AddHours(1),
            new Uri("https://textures.minecraft.net/texture/abcdef", UriKind.Absolute),
            cache));
        CmlLibMicrosoftIdentityClient client = new(
            MicrosoftAuthOptions.Create("11111111-1111-1111-1111-111111111111", null),
            backend);

        var result = await client.SignInInteractivelyAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("Steve", result.Value.PlayerName);
        Assert.Equal("5627dd98-e6be-3c21-b8a8-e92344183641", result.Value.ProfileUuid);
        Assert.Equal("https://textures.minecraft.net/texture/abcdef", result.Value.SkinUri?.AbsoluteUri);
        Assert.Equal("[SECRET]", result.Value.Session.AccessToken.ToString());
        Assert.DoesNotContain("access-token", result.Value.ToString(), StringComparison.Ordinal);
        Assert.Equal(cache, result.Value.Cache.Bytes.ToArray());
    }

    [Theory]
    [InlineData(0, "AUTH_CANCELLED", false)]
    [InlineData(1, "AUTH_STATE_INVALID", false)]
    [InlineData(2, "AUTH_XSTS_REJECTED", false)]
    [InlineData(3, "AUTH_OWNERSHIP_REQUIRED", false)]
    [InlineData(5, "AUTH_SESSION_EXPIRED", false)]
    [InlineData(6, "AUTH_NETWORK_FAILED", true)]
    public async Task BackendFailuresMapToStableCodes(
        int kindValue,
        string expectedCode,
        bool retryable)
    {
        RecordingBackend backend = new(MicrosoftAuthBackendResult.FromFailure(
            new MicrosoftAuthFailure((MicrosoftAuthFailureKind)kindValue, HttpStatusCode: null, Classification: "safe")));
        CmlLibMicrosoftIdentityClient client = new(
            MicrosoftAuthOptions.Create("11111111-1111-1111-1111-111111111111", null),
            backend);

        var result = await client.SignInInteractivelyAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Problem?.Code);
        Assert.Equal(retryable, result.Problem?.IsRetryable);
        Assert.DoesNotContain(
            result.Problem?.SafeContext.Values ?? [],
            value => value.Contains("safe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProfileFailureIsRetryableOnlyForServerErrors()
    {
        RecordingBackend backend = new(MicrosoftAuthBackendResult.FromFailure(
            new MicrosoftAuthFailure(MicrosoftAuthFailureKind.ProfileUnavailable, 500, "profile")));
        CmlLibMicrosoftIdentityClient client = new(
            MicrosoftAuthOptions.Create("11111111-1111-1111-1111-111111111111", null),
            backend);

        var result = await client.SignInInteractivelyAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_PROFILE_UNAVAILABLE", result.Problem?.Code);
        Assert.True(result.Problem?.IsRetryable);
        Assert.DoesNotContain(
            result.Problem?.SafeContext.Values ?? [],
            value => value.Contains("profile", StringComparison.Ordinal));
    }

    [Fact]
    public void SecretMaterialIsDisposableAndNeverPrintsBytes()
    {
        SecretMaterial material = new([10, 20, 30]);

        Assert.Equal("[SECRET]", material.ToString());
        Assert.Equal(new byte[] { 10, 20, 30 }, material.Bytes.ToArray());

        material.Dispose();

        Assert.Empty(material.Bytes.ToArray());
    }

    private sealed class RecordingBackend(MicrosoftAuthBackendResult? result = null) : IMicrosoftAuthBackend
    {
        private readonly MicrosoftAuthBackendResult result = result ?? MicrosoftAuthBackendResult.FromFailure(
            new MicrosoftAuthFailure(MicrosoftAuthFailureKind.NetworkFailed));

        public int InteractiveCalls { get; private set; }

        public Task<MicrosoftAuthBackendResult> SignInInteractivelyAsync(
            MicrosoftAuthOptions options,
            CancellationToken cancellationToken)
        {
            InteractiveCalls++;
            return Task.FromResult(result);
        }

        public Task<MicrosoftAuthBackendResult> RefreshSilentlyAsync(
            MicrosoftAuthOptions options,
            ReadOnlyMemory<byte> cache,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
