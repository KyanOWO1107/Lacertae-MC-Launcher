using System.Security.Cryptography;
using Lacertae.Application.Install;
using Lacertae.Application.Launch;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Launch;

public sealed class LaunchPreflightTests
{
    [Fact]
    public async Task ExecuteAsyncReportsDamagedArtifactsAndRepairAction()
    {
        string root = Path.Combine(Path.GetTempPath(), "lacertae-preflight-" + Guid.NewGuid().ToString("N"));
        string artifactPath = Path.Combine(root, "versions", "fixture-child", "fixture-child.json");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, "valid");
        string javaPath = Path.Combine(root, "java", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.WriteAllText(javaPath, "fixture");
        DownloadArtifact artifact = DownloadArtifact.Create(
            ArtifactKind.VersionMetadata,
            new Uri("https://example.test/fixture-child.json"),
            "versions/fixture-child/fixture-child.json",
            new FileInfo(artifactPath).Length,
            [new ArtifactHash("sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))))]);
        LaunchPlan plan = CreatePlan(root, [artifact]);
        FakeVerifier verifier = new();

        var ready = await new LaunchPreflight(verifier, new FakeEnvironment()).ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);
        Assert.True(ready.IsSuccess, ready.Problem?.Code);
        Assert.True(ready.Value.IsReady);
        Assert.Empty(ready.Value.MissingOrDamagedArtifactIds);

        File.WriteAllText(artifactPath, "damaged");
        var damaged = await new LaunchPreflight(verifier, new FakeEnvironment()).ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);
        Assert.True(damaged.IsSuccess, damaged.Problem?.Code);
        Assert.False(damaged.Value.IsReady);
        Assert.Contains(artifact.ArtifactId, damaged.Value.MissingOrDamagedArtifactIds);
        Assert.Contains("action.version.repair", damaged.Value.SuggestedActionKeys);
    }

    private static LaunchPlan CreatePlan(string root, IReadOnlyList<DownloadArtifact> artifacts) => new(
        Guid.NewGuid().ToString("N"),
        "root-1",
        "fixture-child",
        "fixture-child",
        root,
        Path.Combine(root, "versions", "fixture-child"),
        "java-17",
        Path.Combine(root, "java", "bin", "java.exe"),
        17,
        "account-1",
        AccountType.Offline,
        "Steve",
        "5627dd98-e6be-3c21-b8a8-e92344183641",
        new AuthSession("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641", new SensitiveString("token"), "legacy", null, null),
        1024,
        2048,
        [],
        [],
        [],
        artifacts,
        LaunchDisposition.KeepLauncherOpen,
        DateTimeOffset.UtcNow);

    private sealed class FakeVerifier : IGameFileVerifier
    {
        public async Task<Lacertae.Domain.Results.Result<bool>> VerifyAsync(
            DownloadArtifact artifact,
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length != artifact.ExpectedSize)
            {
                return Lacertae.Domain.Results.Result<bool>.Success(false);
            }

            string expected = artifact.Hashes.Single().NormalizedHexDigest;
            string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(filePath, cancellationToken)));
            return Lacertae.Domain.Results.Result<bool>.Success(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FakeEnvironment : IInstallEnvironment
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool IsDirectoryWritable(string path) => true;
        public long GetAvailableFreeBytes(string path) => 1024L * 1024 * 1024;
    }
}
