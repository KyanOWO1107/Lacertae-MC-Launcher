using Lacertae.Desktop.Configuration;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.Tests.Configuration;

public sealed class OAuthClientRegistrationLoaderTests
{
    private const string ClientId = "11111111-1111-1111-1111-111111111111";
    private const string Authority = "https://login.microsoftonline.com/consumers";

    [Fact]
    public void MissingEnvironmentAndFileReturnsUnconfiguredWithoutAProblem()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            Result<OAuthClientRegistration?> result = new OAuthClientRegistrationLoader(
                directory,
                static () => null).Load();

            Assert.True(result.IsSuccess);
            Assert.Null(result.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentClientIdTakesPrecedenceOverTheLocalFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "oauth.local.json"),
                "{\"clientId\":\"22222222-2222-2222-2222-222222222222\"}");

            Result<OAuthClientRegistration?> result = new OAuthClientRegistrationLoader(
                directory,
                () => ClientId).Load();

            Assert.True(result.IsSuccess);
            Assert.Equal(ClientId, result.Value?.ClientId);
            Assert.Equal(Authority, result.Value?.Authority);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LocalFileAcceptsOnlyPublicClientIdAndAuthority()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "oauth.local.json"),
                "{\"clientId\":\"" + ClientId + "\",\"authority\":\"https://login.microsoftonline.com/consumers/\"}");

            Result<OAuthClientRegistration?> result = new OAuthClientRegistrationLoader(
                directory,
                static () => null).Load();

            Assert.True(result.IsSuccess);
            Assert.Equal(ClientId, result.Value?.ClientId);
            Assert.Equal(Authority, result.Value?.Authority);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"clientSecret\":\"secret\"}")]
    [InlineData("{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"redirectUri\":\"http://localhost\"}")]
    [InlineData("{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"unexpected\":true}")]
    public void LocalFileRejectsSecretsRedirectsAndUnknownFields(string json)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "oauth.local.json"), json);

            Result<OAuthClientRegistration?> result = new OAuthClientRegistrationLoader(
                directory,
                static () => null).Load();

            Assert.False(result.IsSuccess);
            Assert.Equal("AUTH_MICROSOFT_CONFIG_INVALID", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    public void EnvironmentClientIdMustBeARealPublicClientIdentifier(string clientId)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            Result<OAuthClientRegistration?> result = new OAuthClientRegistrationLoader(
                directory,
                () => clientId).Load();

            Assert.False(result.IsSuccess);
            Assert.Equal("AUTH_MICROSOFT_CLIENT_ID_INVALID", result.Problem?.Code);
            Assert.DoesNotContain(clientId, result.Problem?.SafeContext.Values ?? []);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"authority\":\"http://login.microsoftonline.com/consumers\"}")]
    [InlineData("{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"authority\":\"https://example.test/consumers\"}")]
    [InlineData("{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"authority\":\"https://login.microsoftonline.com/common\"}")]
    public void LocalFileRejectsNonConsumerAuthorities(string json)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "oauth.local.json"), json);

            Result<OAuthClientRegistration?> result = new OAuthClientRegistrationLoader(
                directory,
                static () => null).Load();

            Assert.False(result.IsSuccess);
            Assert.Equal("AUTH_MICROSOFT_CONFIG_INVALID", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory() =>
        Directory.CreateTempSubdirectory("lacertae-oauth-").FullName;
}
