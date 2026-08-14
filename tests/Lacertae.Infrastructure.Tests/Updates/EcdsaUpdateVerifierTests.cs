using System.Numerics;
using System.Security.Cryptography;
using Lacertae.Application.Updates;
using Lacertae.Domain.Updates;
using Lacertae.Infrastructure.Updates;

namespace Lacertae.Infrastructure.Tests.Updates;

public sealed class EcdsaUpdateVerifierTests
{
    [Fact]
    public void VerifyAcceptsValidLowSSignatureAndCanonicalizesObjectOrder()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        UpdateManifest first = Manifest(
            notes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["zh-CN"] = "更新说明",
                ["en-US"] = "Release notes",
            });
        UpdateManifest reordered = first with
        {
            ReleaseNotes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en-US"] = "Release notes",
                ["zh-CN"] = "更新说明",
            },
        };
        Assert.Equal(
            EcdsaUpdateVerifier.Canonicalize(first),
            EcdsaUpdateVerifier.Canonicalize(reordered));

        byte[] canonical = EcdsaUpdateVerifier.Canonicalize(first);
        byte[] signature = SignLowS(key, canonical);
        EcdsaUpdateVerifier verifier = new(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["test-key"] = key.ExportSubjectPublicKeyInfo(),
            });

        var result = verifier.Verify(new UpdateManifestEnvelope(first, signature));

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(canonical, result.Value.CanonicalBytes);
    }

    [Fact]
    public void VerifyRejectsChangedContentUnknownKeyAndFutureClock()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        UpdateManifest manifest = Manifest();
        EcdsaUpdateVerifier verifier = new(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["test-key"] = key.ExportSubjectPublicKeyInfo(),
            });
        byte[] signature = SignLowS(key, EcdsaUpdateVerifier.Canonicalize(manifest));

        UpdateManifest changed = manifest with { Version = "1.3.0" };
        var changedResult = verifier.Verify(new UpdateManifestEnvelope(changed, signature));
        Assert.False(changedResult.IsSuccess);
        Assert.Equal("UPDATE_SIGNATURE_INVALID", changedResult.Problem?.Code);

        var unknownKeyResult = verifier.Verify(new UpdateManifestEnvelope(
            manifest with { KeyId = "unknown" },
            signature));
        Assert.False(unknownKeyResult.IsSuccess);
        Assert.Equal("UPDATE_KEY_UNKNOWN", unknownKeyResult.Problem?.Code);

        var futureResult = verifier.Verify(new UpdateManifestEnvelope(
            manifest with { PublishedUtc = DateTimeOffset.UtcNow.AddHours(1) },
            signature));
        Assert.False(futureResult.IsSuccess);
        Assert.Equal("UPDATE_MANIFEST_CLOCK_SKEW", futureResult.Problem?.Code);
    }

    [Fact]
    public void VerifyRejectsHighSAndInvalidSchema()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        UpdateManifest manifest = Manifest();
        byte[] signature = SignLowS(key, EcdsaUpdateVerifier.Canonicalize(manifest));
        byte[] highS = ToHighS(signature);
        EcdsaUpdateVerifier verifier = new(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["test-key"] = key.ExportSubjectPublicKeyInfo(),
            });

        var highSResult = verifier.Verify(new UpdateManifestEnvelope(manifest, highS));
        Assert.False(highSResult.IsSuccess);
        Assert.Equal("UPDATE_SIGNATURE_NON_CANONICAL", highSResult.Problem?.Code);

        var schemaResult = verifier.Verify(new UpdateManifestEnvelope(
            manifest with { SchemaVersion = 2 },
            signature));
        Assert.False(schemaResult.IsSuccess);
        Assert.Equal("UPDATE_MANIFEST_INVALID", schemaResult.Problem?.Code);
    }

    private static UpdateManifest Manifest(
        IReadOnlyDictionary<string, string>? notes = null) => new(
        1,
        "test-key",
        UpdateChannel.Stable,
        "1.2.0",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        "1.0.0",
        notes ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["en-US"] = "Release notes" },
        new Uri("https://updates.example.test/releases/1.2.0"),
        new UpdatePackage(
            "win-x64",
            new Uri("https://updates.example.test/packages/1.2.0.zip"),
            128,
            new string('a', 64),
            new string('b', 64)));

    private static byte[] SignLowS(ECDsa key, byte[] data)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            byte[] signature = key.SignData(
                data,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            BigInteger s = new(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
            if (s <= HalfCurveOrder)
            {
                return signature;
            }
        }

        throw new InvalidOperationException("Could not generate a low-S signature.");
    }

    private static byte[] ToHighS(byte[] signature)
    {
        byte[] result = signature.ToArray();
        BigInteger s = new(result.AsSpan(32), isUnsigned: true, isBigEndian: true);
        BigInteger high = CurveOrder - s;
        high.TryWriteBytes(result.AsSpan(32), out _, isUnsigned: true, isBigEndian: true);
        return result;
    }

    private static readonly BigInteger CurveOrder = new(
        Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
    private static readonly BigInteger HalfCurveOrder = CurveOrder >> 1;
}
