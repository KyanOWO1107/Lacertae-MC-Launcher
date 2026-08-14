using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Versions;

public sealed class SaveVersionOverrideTests
{
    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    public async Task RejectsArgumentsContainingLineBreaks(string argument)
    {
        FakeRepository repository = new();
        SaveVersionOverride save = new(repository);

        Result<Unit> result = await save.ExecuteAsync(CreateOverride([argument]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_OVERRIDE_INVALID", result.Problem?.Code);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task RejectsArgumentsLargerThanEightKiB()
    {
        FakeRepository repository = new();
        SaveVersionOverride save = new(repository);

        Result<Unit> result = await save.ExecuteAsync(
            CreateOverride([new string('x', 8193)]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_OVERRIDE_INVALID", result.Problem?.Code);
        Assert.Empty(repository.Saved);
    }

    private static VersionOverride CreateOverride(IReadOnlyList<string> jvmArguments) =>
        new("root", "1.21.1", null, IsolationOverride.Inherit, null, null, null, null, null, jvmArguments, []);

    private sealed class FakeRepository : IVersionOverrideRepository
    {
        public List<VersionOverride> Saved { get; } = [];

        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>(Saved);

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken)
        {
            Saved.Add(versionOverride);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
