using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lacertae.Application.Updates;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Infrastructure.Updates;

/// <summary>
/// Verifies detached P-256/SHA-256 signatures over RFC 8785-style canonical
/// manifest bytes. Production key material is supplied by the host; this type
/// does not contain a default public key.
/// </summary>
public sealed class EcdsaUpdateVerifier : IUpdateVerifier
{
    private const int SignatureSize = 64;
    private static readonly BigInteger CurveOrder = FromHex(
        "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger HalfCurveOrder = CurveOrder >> 1;
    private static readonly System.Text.RegularExpressions.Regex LocaleRegex = new(
        "^[a-z]{2,3}(?:-[A-Z]{2})?$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private readonly Dictionary<string, byte[]> publicKeys;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan maximumClockSkew;

    public EcdsaUpdateVerifier(
        IReadOnlyDictionary<string, byte[]> publicKeys,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumClockSkew = null)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        this.publicKeys = publicKeys.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumClockSkew = maximumClockSkew ?? TimeSpan.FromMinutes(10);
        if (this.maximumClockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClockSkew));
        }
    }

    public Result<VerifiedUpdateManifest> Verify(UpdateManifestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Result<Unit> validation = ValidateManifest(envelope.Manifest);
        if (!validation.IsSuccess)
        {
            return Result<VerifiedUpdateManifest>.Failure(validation.Problem!);
        }

        if (!publicKeys.TryGetValue(envelope.Manifest.KeyId, out byte[]? publicKey))
        {
            return Failure<VerifiedUpdateManifest>("UPDATE_KEY_UNKNOWN", retryable: false);
        }

        if (envelope.Signature is null || envelope.Signature.Length != SignatureSize || !IsLowS(envelope.Signature))
        {
            return Failure<VerifiedUpdateManifest>("UPDATE_SIGNATURE_NON_CANONICAL", retryable: false);
        }

        byte[] canonicalBytes = Canonicalize(envelope.Manifest);
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out int bytesRead);
            if (bytesRead != publicKey.Length || !ecdsa.VerifyData(
                    canonicalBytes,
                    envelope.Signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Failure<VerifiedUpdateManifest>("UPDATE_SIGNATURE_INVALID", retryable: false);
            }
        }
        catch (CryptographicException)
        {
            return Failure<VerifiedUpdateManifest>("UPDATE_SIGNATURE_INVALID", retryable: false);
        }

        return Result<VerifiedUpdateManifest>.Success(new VerifiedUpdateManifest(
            envelope.Manifest,
            canonicalBytes,
            envelope.Signature.ToArray()));
    }

    public static byte[] Canonicalize(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        StringBuilder builder = new();
        WriteObject(builder, new SortedDictionary<string, Action<StringBuilder>>(StringComparer.Ordinal)
        {
            ["channel"] = target => WriteString(target, manifest.Channel switch
            {
                UpdateChannel.Stable => "stable",
                UpdateChannel.Preview => "preview",
                UpdateChannel.Test => "test",
                UpdateChannel.Nightly => "nightly",
                _ => throw new InvalidOperationException("Unknown update channel."),
            }),
            ["keyId"] = target => WriteString(target, manifest.KeyId),
            ["minimumLauncherVersion"] = target => WriteString(target, manifest.MinimumLauncherVersion),
            ["package"] = target => WritePackage(target, manifest.Package),
            ["publishedUtc"] = target => WriteString(target, manifest.PublishedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
            ["releaseNotes"] = target => WriteReleaseNotes(target, manifest.ReleaseNotes),
            ["releaseNotesUrl"] = target => WriteString(target, manifest.ReleaseNotesUrl.AbsoluteUri),
            ["schemaVersion"] = target => target.Append(manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)),
            ["version"] = target => WriteString(target, manifest.Version),
        });
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WritePackage(StringBuilder builder, UpdatePackage package)
    {
        WriteObject(builder, new SortedDictionary<string, Action<StringBuilder>>(StringComparer.Ordinal)
        {
            ["fileManifestSha256"] = target => WriteString(target, package.FileManifestSha256),
            ["runtime"] = target => WriteString(target, package.Runtime),
            ["sha256"] = target => WriteString(target, package.Sha256),
            ["size"] = target => target.Append(package.Size.ToString(CultureInfo.InvariantCulture)),
            ["url"] = target => WriteString(target, package.Url.AbsoluteUri),
        });
    }

    private static void WriteReleaseNotes(StringBuilder builder, IReadOnlyDictionary<string, string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        WriteObject(builder, notes
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => (Action<StringBuilder>)(target => WriteString(target, pair.Value)), StringComparer.Ordinal));
    }

    private static void WriteObject(
        StringBuilder builder,
        IReadOnlyDictionary<string, Action<StringBuilder>> properties)
    {
        builder.Append('{');
        bool first = true;
        foreach (KeyValuePair<string, Action<StringBuilder>> property in properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteString(builder, property.Key);
            builder.Append(':');
            property.Value(builder);
        }

        builder.Append('}');
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append(JsonSerializer.Serialize(value, StringOptions));
    }

    private Result<Unit> ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != UpdateManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.KeyId) || manifest.KeyId.Length > 64 ||
            !manifest.KeyId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ||
            !Enum.IsDefined(manifest.Channel) ||
            !UpdateManifest.IsValidSemanticVersion(manifest.Version) ||
            !UpdateManifest.IsValidSemanticVersion(manifest.MinimumLauncherVersion) ||
            manifest.PublishedUtc > timeProvider.GetUtcNow() + maximumClockSkew ||
            manifest.ReleaseNotes is null || manifest.ReleaseNotes.Count is < 1 or > 16 ||
            manifest.ReleaseNotes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !LocaleRegex.IsMatch(pair.Key) || pair.Value is null || pair.Value.Length > 65536) ||
            !IsHttpsUri(manifest.ReleaseNotesUrl) ||
            manifest.Package is null ||
            manifest.Package.Runtime != UpdateManifest.SupportedRuntime ||
            !IsHttpsUri(manifest.Package.Url) ||
            manifest.Package.Size <= 0 ||
            !IsSha256(manifest.Package.Sha256) ||
            !IsSha256(manifest.Package.FileManifestSha256))
        {
            string code = manifest.PublishedUtc > timeProvider.GetUtcNow() + maximumClockSkew
                ? "UPDATE_MANIFEST_CLOCK_SKEW"
                : "UPDATE_MANIFEST_INVALID";
            return Failure<Unit>(code, retryable: false);
        }

        return Result.Success();
    }

    private static bool IsHttpsUri(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool IsLowS(byte[] signature)
    {
        BigInteger s = new(signature.AsSpan(SignatureSize / 2), isUnsigned: true, isBigEndian: true);
        return s > BigInteger.Zero && s <= HalfCurveOrder;
    }

    private static BigInteger FromHex(string value) =>
        new(Convert.FromHexString(value), isUnsigned: true, isBigEndian: true);

    private static Result<T> Failure<T>(string code, bool retryable) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Update,
        "problem.update.signature_failed",
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.update.retry"]));

    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
