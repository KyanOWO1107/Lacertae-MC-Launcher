using Lacertae.Application.Updates;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Application.Tests.Updates;

public sealed class CheckForUpdatesTests
{
    [Fact]
    public async Task DisabledCheckDoesNotCallSource()
    {
        FakeSource source = new();
        CheckForUpdates check = new(source, new FakeVerifier(), enabled: false);

        var result = await check.ExecuteAsync("1.0.0", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdateCheckStatus.Disabled, result.Value.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AvailableAndCurrentVersionsAreComparedSemantically()
    {
        UpdateManifest manifest = Manifest("1.2.0");
        FakeSource source = new(manifest);
        CheckForUpdates check = new(source, new FakeVerifier());

        var available = await check.ExecuteAsync("1.1.9", TestContext.Current.CancellationToken);
        var current = await check.ExecuteAsync("1.2.0", TestContext.Current.CancellationToken);

        Assert.True(available.IsSuccess);
        Assert.Equal(UpdateCheckStatus.Available, available.Value.Status);
        Assert.NotNull(available.Value.Update);
        Assert.True(current.IsSuccess);
        Assert.Equal(UpdateCheckStatus.Current, current.Value.Status);
    }

    [Fact]
    public async Task NetworkAndSignatureFailuresRemainNonBlockingStatuses()
    {
        Problem networkProblem = new(
            "UPDATE_CHECK_FAILED",
            ProblemStage.Update,
            "problem.update.check_failed",
            true,
            "network",
            ["action.update.retry"]);
        CheckForUpdates networkCheck = new(new FakeSource(problem: networkProblem), new FakeVerifier());
        var network = await networkCheck.ExecuteAsync("1.0.0", TestContext.Current.CancellationToken);

        CheckForUpdates signatureCheck = new(
            new FakeSource(Manifest("1.2.0")),
            new FakeVerifier(Problem("UPDATE_SIGNATURE_INVALID", false)));
        var signature = await signatureCheck.ExecuteAsync("1.0.0", TestContext.Current.CancellationToken);

        Assert.True(network.IsSuccess);
        Assert.Equal(UpdateCheckStatus.Failed, network.Value.Status);
        Assert.True(network.Value.Problem!.IsRetryable);
        Assert.True(signature.IsSuccess);
        Assert.Equal(UpdateCheckStatus.Failed, signature.Value.Status);
        Assert.Equal("UPDATE_SIGNATURE_INVALID", signature.Value.Problem?.Code);
    }

    [Fact]
    public async Task MismatchedChannelIsRejectedBeforeTrustResultIsUsed()
    {
        FakeSource source = new(Manifest("1.2.0") with { Channel = UpdateChannel.Preview });
        CheckForUpdates check = new(source, new FakeVerifier());

        var result = await check.ExecuteAsync("1.0.0", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdateCheckStatus.Failed, result.Value.Status);
        Assert.Equal("UPDATE_CHANNEL_MISMATCH", result.Value.Problem?.Code);
    }

    private static UpdateManifest Manifest(string version) => new(
        1,
        "test-key",
        UpdateChannel.Stable,
        version,
        DateTimeOffset.UtcNow.AddMinutes(-1),
        "1.0.0",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["en-US"] = "Notes" },
        new Uri("https://updates.example.test/notes"),
        new UpdatePackage("win-x64", new Uri("https://updates.example.test/package.zip"), 10, new string('a', 64), new string('b', 64)));

    private static Problem Problem(string code, bool retryable) => new(
        code,
        ProblemStage.Update,
        "problem.update.check_failed",
        retryable,
        "test",
        ["action.update.retry"]);

    private sealed class FakeSource : IUpdateManifestSource
    {
        private readonly UpdateManifest? manifest;
        private readonly Problem? problem;

        public FakeSource(UpdateManifest? manifest = null, Problem? problem = null)
        {
            this.manifest = manifest;
            this.problem = problem;
        }

        public int Calls { get; private set; }

        public Task<Result<UpdateManifestEnvelope>> FetchAsync(UpdateChannel channel, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(problem is not null
                ? Result<UpdateManifestEnvelope>.Failure(problem)
                : Result<UpdateManifestEnvelope>.Success(new UpdateManifestEnvelope(manifest!, [1, 2, 3])));
        }
    }

    private sealed class FakeVerifier(Problem? problem = null) : IUpdateVerifier
    {
        public Result<VerifiedUpdateManifest> Verify(UpdateManifestEnvelope envelope) => problem is null
            ? Result<VerifiedUpdateManifest>.Success(new VerifiedUpdateManifest(
                envelope.Manifest,
                [1, 2, 3],
                envelope.Signature))
            : Result<VerifiedUpdateManifest>.Failure(problem);
    }
}
