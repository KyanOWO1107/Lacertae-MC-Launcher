using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Downloads;

public sealed class DownloadSourceSelectorTests
{
    [Fact]
    public void SelectorPutsOfficialFirstWhenSourceIsAutomatic()
    {
        DownloadSourceId officialId = new("official");
        DownloadSourceId mirrorId = new("mirror-a");
        FakeSource mirror = new(mirrorId, false, "https://mirror.example.test/artifact");
        FakeSource official = new(officialId, true, "https://official.example.test/artifact");
        DownloadSourceSelector selector = new([mirror, official]);

        Result<IReadOnlyList<DownloadCandidate>> result = selector.Select(
            Artifact(),
            DownloadSourcePreference.Automatic,
            temporaryFallbackApproved: false,
            "corr-automatic");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["official", "mirror-a"], result.Value.Select(static candidate => candidate.SourceId.Value));
        Assert.Equal(1, official.MapCalls);
        Assert.Equal(1, mirror.MapCalls);
    }

    [Fact]
    public void SelectorUsesOnlyPinnedSourceUntilUserApprovesTemporaryFallback()
    {
        DownloadSourceId officialId = new("official");
        DownloadSourceId mirrorId = new("mirror-a");
        FakeSource official = new(officialId, true, "https://official.example.test/artifact");
        FakeSource mirror = new(mirrorId, false, "https://mirror.example.test/artifact");
        DownloadSourceSelector selector = new([official, mirror]);

        Result<IReadOnlyList<DownloadCandidate>> pinned = selector.Select(
            Artifact(),
            DownloadSourcePreference.Pinned(mirrorId),
            temporaryFallbackApproved: false,
            "corr-pinned");
        Result<IReadOnlyList<DownloadCandidate>> approved = selector.Select(
            Artifact(),
            DownloadSourcePreference.Pinned(mirrorId),
            temporaryFallbackApproved: true,
            "corr-approved");

        Assert.True(pinned.IsSuccess, pinned.Problem?.Code);
        Assert.Equal(["mirror-a"], pinned.Value.Select(static candidate => candidate.SourceId.Value));
        Assert.True(approved.IsSuccess, approved.Problem?.Code);
        Assert.Equal(["mirror-a", "official"], approved.Value.Select(static candidate => candidate.SourceId.Value));
    }

    [Fact]
    public void SelectorNeverReturnsTwoSourcesForRacing()
    {
        DownloadSourceId officialId = new("official");
        FakeSource firstOfficial = new(officialId, true, "https://official.example.test/first");
        FakeSource duplicateOfficial = new(officialId, true, "https://official.example.test/duplicate");
        DownloadSourceSelector selector = new([firstOfficial, duplicateOfficial]);

        Result<IReadOnlyList<DownloadCandidate>> result = selector.Select(
            Artifact(),
            DownloadSourcePreference.Automatic,
            temporaryFallbackApproved: false,
            "corr-deduplicated");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Single(result.Value);
        Assert.Equal("official", result.Value[0].SourceId.Value);
        Assert.Equal(1, firstOfficial.MapCalls);
        Assert.Equal(0, duplicateOfficial.MapCalls);
    }

    [Fact]
    public void SelectorRejectsUnknownPinnedSourceInsteadOfFallingThrough()
    {
        DownloadSourceSelector selector = new([
            new FakeSource(new DownloadSourceId("official"), true, "https://official.example.test/artifact"),
        ]);

        Result<IReadOnlyList<DownloadCandidate>> result = selector.Select(
            Artifact(),
            DownloadSourcePreference.Pinned(new DownloadSourceId("untrusted")),
            temporaryFallbackApproved: true,
            "corr-unknown");

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_SOURCE_UNAVAILABLE", result.Problem?.Code);
    }

    private static DownloadArtifact Artifact() => DownloadArtifact.Create(
        ArtifactKind.ClientJar,
        new Uri("https://official.example.test/artifact"),
        "client.jar",
        1,
        [new ArtifactHash("sha256", new string('a', 64))]);

    private sealed class FakeSource(DownloadSourceId id, bool isOfficial, string mappedUri) : IDownloadSource
    {
        public DownloadSourceId Id { get; } = id;
        public bool IsOfficial { get; } = isOfficial;
        public int MapCalls { get; private set; }

        public bool CanMap(DownloadArtifact artifact) => true;

        public Result<DownloadCandidate> Map(DownloadArtifact artifact, string correlationId)
        {
            MapCalls++;
            return Result<DownloadCandidate>.Success(new DownloadCandidate(
                Id,
                new Uri(mappedUri),
                IsOfficial,
                SupportsRanges: true));
        }
    }
}
