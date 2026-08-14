using System.Text.Json.Serialization;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;

namespace Lacertae.Domain.Launch;

/// <summary>
/// The complete set of values used for one game process. Collections are copied
/// at construction so UI settings cannot change a plan after it has been frozen.
/// </summary>
public sealed record LaunchPlan
{
    public LaunchPlan(
        string correlationId,
        string gameRootId,
        string versionFolder,
        string versionId,
        string gameRootPath,
        string gameDirectory,
        string javaInstallationId,
        string javaExecutablePath,
        int requiredJavaMajor,
        string accountId,
        AccountType accountType,
        string playerName,
        string profileUuid,
        AuthSession session,
        int minimumMemoryMb,
        int maximumMemoryMb,
        IReadOnlyList<string> structuredJvmArguments,
        IReadOnlyList<string> userJvmArguments,
        IReadOnlyList<string> gameArguments,
        IReadOnlyList<DownloadArtifact> requiredFiles,
        LaunchDisposition disposition,
        DateTimeOffset createdUtc,
        string? versionDisplayName = null,
        JavaArchitecture javaArchitecture = JavaArchitecture.Unknown)
    {
        CorrelationId = Require(correlationId, nameof(correlationId));
        GameRootId = Require(gameRootId, nameof(gameRootId));
        VersionFolder = Require(versionFolder, nameof(versionFolder));
        VersionId = Require(versionId, nameof(versionId));
        GameRootPath = NormalizePath(gameRootPath, nameof(gameRootPath));
        GameDirectory = NormalizePath(gameDirectory, nameof(gameDirectory));
        JavaInstallationId = Require(javaInstallationId, nameof(javaInstallationId));
        JavaExecutablePath = NormalizePath(javaExecutablePath, nameof(javaExecutablePath));
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredJavaMajor, 1);
        RequiredJavaMajor = requiredJavaMajor;

        AccountId = Require(accountId, nameof(accountId));
        if (!Enum.IsDefined(accountType))
        {
            throw new ArgumentOutOfRangeException(nameof(accountType));
        }

        AccountType = accountType;

        PlayerName = Require(playerName, nameof(playerName));
        ProfileUuid = Require(profileUuid, nameof(profileUuid));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (!string.Equals(PlayerName, Session.PlayerName, StringComparison.Ordinal) ||
            !string.Equals(ProfileUuid, Session.ProfileUuid, StringComparison.Ordinal))
        {
            throw new ArgumentException("The authentication session does not match the selected account.", nameof(session));
        }

        if (minimumMemoryMb < 256 || maximumMemoryMb < minimumMemoryMb)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumMemoryMb));
        }

        MinimumMemoryMb = minimumMemoryMb;
        MaximumMemoryMb = maximumMemoryMb;
        StructuredJvmArguments = CopyArguments(structuredJvmArguments, nameof(structuredJvmArguments));
        UserJvmArguments = CopyArguments(userJvmArguments, nameof(userJvmArguments));
        GameArguments = CopyArguments(gameArguments, nameof(gameArguments));
        RequiredFiles = requiredFiles?.ToArray() ?? throw new ArgumentNullException(nameof(requiredFiles));
        if (RequiredFiles.Any(static artifact => artifact is null))
        {
            throw new ArgumentException("Required files cannot contain null entries.", nameof(requiredFiles));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Disposition = disposition;
        CreatedUtc = createdUtc;
        VersionDisplayName = string.IsNullOrWhiteSpace(versionDisplayName) ? VersionFolder : versionDisplayName;
        JavaArchitecture = Enum.IsDefined(javaArchitecture) ? javaArchitecture : JavaArchitecture.Unknown;
    }

    public string CorrelationId { get; }
    public string GameRootId { get; }
    public string VersionFolder { get; }
    public string VersionId { get; }
    public string GameRootPath { get; }
    public string GameDirectory { get; }
    public string JavaInstallationId { get; }
    public string JavaExecutablePath { get; }
    public int RequiredJavaMajor { get; }
    public string AccountId { get; }
    public AccountType AccountType { get; }
    public string PlayerName { get; }
    public string ProfileUuid { get; }

    [JsonIgnore]
    public AuthSession Session { get; }

    public int MinimumMemoryMb { get; }
    public int MaximumMemoryMb { get; }
    public IReadOnlyList<string> StructuredJvmArguments { get; }
    public IReadOnlyList<string> UserJvmArguments { get; }
    public IReadOnlyList<string> GameArguments { get; }
    public IReadOnlyList<DownloadArtifact> RequiredFiles { get; }
    public LaunchDisposition Disposition { get; }
    public DateTimeOffset CreatedUtc { get; }
    public string VersionDisplayName { get; }
    public JavaArchitecture JavaArchitecture { get; }

    // Compatibility aliases for the M0 launch-plan spike. New code should use
    // the explicit structured/user lists above.
    [JsonIgnore]
    public string JavaPath => JavaExecutablePath;

    [JsonIgnore]
    public JvmArgumentSet JvmArguments => new(
        StructuredJvmArguments.Where(static argument => IsMemoryArgument(argument)).ToArray(),
        StructuredJvmArguments.Where(static argument => !IsMemoryArgument(argument)).ToArray(),
        UserJvmArguments);

    [JsonIgnore]
    public IReadOnlyList<string> FlattenedJvmArguments => [.. StructuredJvmArguments, .. UserJvmArguments];

    public override string ToString() =>
        $"LaunchPlan({CorrelationId}, {GameRootId}/{VersionFolder}, account={AccountId}, [REDACTED SESSION])";

    private static string[] CopyArguments(IReadOnlyList<string>? arguments, string parameterName)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (arguments.Any(static argument => string.IsNullOrWhiteSpace(argument) || argument.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            throw new ArgumentException("Arguments must be non-empty single-line tokens.", parameterName);
        }

        return arguments.ToArray();
    }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be blank.", parameterName)
            : value;

    private static string NormalizePath(string value, string parameterName)
    {
        Require(value, parameterName);
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Path is invalid.", parameterName, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ArgumentException("Path is invalid.", parameterName, exception);
        }
    }

    private static bool IsMemoryArgument(string argument) =>
        argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase);
}
