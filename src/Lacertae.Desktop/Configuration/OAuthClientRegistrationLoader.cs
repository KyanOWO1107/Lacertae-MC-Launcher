using System.Security;
using System.Text.Json;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.Configuration;

public sealed record OAuthClientRegistration(string ClientId, string Authority)
{
    public const string ConsumerAuthority = "https://login.microsoftonline.com/consumers";

    public const string SystemBrowserRedirectUri = "http://localhost";
}

public sealed class OAuthClientRegistrationLoader
{
    public const string EnvironmentVariable = "LACERTAE_MICROSOFT_CLIENT_ID";
    public const string LocalFileName = "oauth.local.json";

    private readonly string executableDirectory;
    private readonly Func<string?> readEnvironmentClientId;

    public OAuthClientRegistrationLoader(
        string executableDirectory,
        Func<string?>? readEnvironmentClientId = null)
    {
        if (string.IsNullOrWhiteSpace(executableDirectory) || !Path.IsPathRooted(executableDirectory))
        {
            throw new ArgumentException("The executable directory must be an absolute path.", nameof(executableDirectory));
        }

        this.executableDirectory = Path.GetFullPath(executableDirectory);
        this.readEnvironmentClientId = readEnvironmentClientId ??
            (() => Environment.GetEnvironmentVariable(EnvironmentVariable));
    }

    public Result<OAuthClientRegistration?> Load()
    {
        string? environmentClientId = readEnvironmentClientId();
        if (!string.IsNullOrWhiteSpace(environmentClientId))
        {
            return ParseClientId(environmentClientId);
        }

        string localPath = Path.Combine(executableDirectory, LocalFileName);
        string json;
        try
        {
            json = File.ReadAllText(localPath);
        }
        catch (FileNotFoundException)
        {
            return Result<OAuthClientRegistration?>.Success(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Result<OAuthClientRegistration?>.Success(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Result<OAuthClientRegistration?>.Failure(Problem(
                "AUTH_MICROSOFT_CONFIG_READ_FAILED",
                "problem.auth.microsoft_config_read_failed",
                isRetryable: false));
        }
        catch (IOException)
        {
            return Result<OAuthClientRegistration?>.Failure(Problem(
                "AUTH_MICROSOFT_CONFIG_READ_FAILED",
                "problem.auth.microsoft_config_read_failed",
                isRetryable: true));
        }
        catch (SecurityException)
        {
            return Result<OAuthClientRegistration?>.Failure(Problem(
                "AUTH_MICROSOFT_CONFIG_READ_FAILED",
                "problem.auth.microsoft_config_read_failed",
                isRetryable: false));
        }

        return ParseLocalFile(json);
    }

    private static Result<OAuthClientRegistration?> ParseLocalFile(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidConfiguration();
            }

            string? clientId = null;
            string authority = OAuthClientRegistration.ConsumerAuthority;
            HashSet<string> propertyNames = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    return InvalidConfiguration();
                }

                switch (property.Name)
                {
                    case "clientId":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return InvalidConfiguration();
                        }

                        clientId = property.Value.GetString();
                        break;
                    case "authority":
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            !TryNormalizeAuthority(property.Value.GetString(), out authority))
                        {
                            return InvalidConfiguration();
                        }

                        break;
                    default:
                        return InvalidConfiguration();
                }
            }

            if (string.IsNullOrWhiteSpace(clientId))
            {
                return InvalidConfiguration();
            }

            return ParseClientId(clientId, authority);
        }
        catch (JsonException)
        {
            return InvalidConfiguration();
        }
        catch (InvalidOperationException)
        {
            return InvalidConfiguration();
        }
    }

    private static Result<OAuthClientRegistration?> ParseClientId(
        string clientId,
        string authority = OAuthClientRegistration.ConsumerAuthority)
    {
        if (!Guid.TryParse(clientId, out Guid parsed) || parsed == Guid.Empty)
        {
            return Result<OAuthClientRegistration?>.Failure(Problem(
                "AUTH_MICROSOFT_CLIENT_ID_INVALID",
                "problem.auth.microsoft_client_id_invalid",
                isRetryable: false));
        }

        return Result<OAuthClientRegistration?>.Success(new OAuthClientRegistration(
            parsed.ToString("D"),
            authority));
    }

    private static bool TryNormalizeAuthority(string? value, out string authority)
    {
        authority = OAuthClientRegistration.ConsumerAuthority;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/consumers", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort)
        {
            return false;
        }

        return true;
    }

    private static Result<OAuthClientRegistration?> InvalidConfiguration() =>
        Result<OAuthClientRegistration?>.Failure(Problem(
            "AUTH_MICROSOFT_CONFIG_INVALID",
            "problem.auth.microsoft_config_invalid",
            isRetryable: false));

    private static Problem Problem(string code, string messageKey, bool isRetryable) => new(
        code,
        ProblemStage.Configuration,
        messageKey,
        isRetryable,
        Guid.NewGuid().ToString("N"),
        ["action.auth.review"]);
}
