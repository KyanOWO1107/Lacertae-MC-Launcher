using Lacertae.Application.Operations;
using Lacertae.Application.Versions;
using Lacertae.Desktop.ViewModels.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Desktop.Tests.Versions;

public sealed class VersionSettingsViewModelTests
{
    [Fact]
    public async Task SaveUsesDisplayNameIsolationAndOneArgumentPerLine()
    {
        ListedGameVersion listed = CreateListed();
        FakeOverrideRepository repository = new();
        VersionSettingsViewModel viewModel = new(
            Root(),
            listed,
            new SaveVersionOverride(repository));

        viewModel.DisplayNameDraft = "我的版本";
        viewModel.IsolationOverride = IsolationOverride.ForceIsolated;
        viewModel.JvmArgumentsText = "-Dmemory=2G\n-Dname=has spaces\n\n";
        viewModel.GameArgumentsText = "--demo\r\n--username Player";

        Result<Unit> result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        VersionOverride saved = Assert.Single(repository.Values);
        Assert.Equal("我的版本", saved.DisplayName);
        Assert.Equal(IsolationOverride.ForceIsolated, saved.Isolation);
        Assert.Equal(["-Dmemory=2G", "-Dname=has spaces"], saved.JvmArguments);
        Assert.Equal(["--demo", "--username Player"], saved.GameArguments);
    }

    [Fact]
    public async Task NulOrOversizedArgumentShowsInlineLineValidation()
    {
        VersionSettingsViewModel nulViewModel = new(Root(), CreateListed(), new SaveVersionOverride(new FakeOverrideRepository()))
        {
            JvmArgumentsText = "-Dbad\0token",
        };

        Result<Unit> nulResult = await nulViewModel.SaveAsync(CancellationToken.None);

        Assert.False(nulResult.IsSuccess);
        Assert.Equal(1, nulViewModel.ValidationLineIndex);
        Assert.Contains("NUL", nulViewModel.ValidationError!, StringComparison.Ordinal);

        VersionSettingsViewModel oversizedViewModel = new(Root(), CreateListed(), new SaveVersionOverride(new FakeOverrideRepository()))
        {
            JvmArgumentsText = new string('x', 8193),
        };

        Result<Unit> oversizedResult = await oversizedViewModel.SaveAsync(CancellationToken.None);

        Assert.False(oversizedResult.IsSuccess);
        Assert.Equal(1, oversizedViewModel.ValidationLineIndex);
        Assert.Contains("8 KiB", oversizedViewModel.ValidationError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryConflictBlocksSaveWithExplicitProblem()
    {
        VersionSettingsViewModel viewModel = new(Root(), CreateListed(), new SaveVersionOverride(new FakeOverrideRepository()))
        {
            MinimumMemoryText = "4096",
            MaximumMemoryText = "2048",
        };

        Result<Unit> result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_MEMORY_CONFLICT", result.Problem?.Code);
        Assert.Equal(1, viewModel.ValidationLineIndex);
    }

    [Fact]
    public async Task FixedMemoryRequiresBothBounds()
    {
        VersionSettingsViewModel viewModel = new(Root(), CreateListed(), new SaveVersionOverride(new FakeOverrideRepository()))
        {
            MinimumMemoryText = "1024",
        };

        Result<Unit> result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_MEMORY_PAIR_REQUIRED", result.Problem?.Code);
    }

    [Fact]
    public async Task StructuredJvmMemoryArgumentConflictsWithMemoryEditor()
    {
        VersionSettingsViewModel viewModel = new(Root(), CreateListed(), new SaveVersionOverride(new FakeOverrideRepository()))
        {
            JvmArgumentsText = "-Xmx2048M",
        };

        Result<Unit> result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", result.Problem?.Code);
        Assert.Equal(1, viewModel.ValidationLineIndex);
    }

    [Fact]
    public async Task StructuredJvmMemoryArgumentReportsOriginalLineIndexAfterBlankLine()
    {
        VersionSettingsViewModel viewModel = new(Root(), CreateListed(), new SaveVersionOverride(new FakeOverrideRepository()))
        {
            JvmArgumentsText = "\n-Xmx2048M",
        };

        Result<Unit> result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", result.Problem?.Code);
        Assert.Equal(2, viewModel.ValidationLineIndex);
    }

    [Fact]
    public async Task RenameQueriesActiveTaskStoreAndBlocksWhenAnyTaskIsRunning()
    {
        FakeBackgroundTaskStore tasks = new(Result<IReadOnlyList<OperationSnapshot>>.Success(
            [new OperationSnapshot("task", "install", OperationState.Running, null, null)]));
        VersionSettingsViewModel viewModel = new(
            Root(),
            CreateListed(),
            new SaveVersionOverride(new FakeOverrideRepository()),
            new RenameVersionFolder(new FakeOverrideRepository(), new FakeRenameJournal()),
            backgroundTaskStore: tasks);

        Result<VersionRenamePlan> result = await viewModel.PrepareRenameAsync(false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_RENAME_ACTIVE_TASK", result.Problem?.Code);
        Assert.Equal(1, tasks.CallCount);
    }

    [Fact]
    public async Task RenameBlocksConservativelyWhenActiveTaskStateCannotBeRead()
    {
        FakeBackgroundTaskStore tasks = new(Result<IReadOnlyList<OperationSnapshot>>.Failure(new Problem(
            "BACKGROUND_TASK_UNAVAILABLE",
            ProblemStage.Storage,
            "problem.background_task.unavailable",
            true,
            "test",
            [])));
        VersionSettingsViewModel viewModel = new(
            Root(),
            CreateListed(),
            new SaveVersionOverride(new FakeOverrideRepository()),
            new RenameVersionFolder(new FakeOverrideRepository(), new FakeRenameJournal()),
            backgroundTaskStore: tasks);

        Result<VersionRenamePlan> result = await viewModel.PrepareRenameAsync(false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_RENAME_TASK_STATE_UNAVAILABLE", result.Problem?.Code);
        Assert.Equal(result.Problem?.Code, viewModel.ValidationErrorCode);
    }

    private static GameRoot Root() =>
        new("root", Path.Combine(Path.GetTempPath(), "lacertae-root"), "Root", GameRootAvailability.Available, null);

    private static ListedGameVersion CreateListed() =>
        new(
            new GameVersionDescriptor("root", "1.21.1", "1.21.1", "release", null, new JavaRequirement("Minecraft", 21)),
            "1.21.1",
            new VersionOverride("root", "1.21.1", null, IsolationOverride.Inherit, null, null, null, null, null, [], []),
            new IsolationDecision(false, false, "isolation.policy.disabled"));

    private sealed class FakeOverrideRepository : IVersionOverrideRepository
    {
        public List<VersionOverride> Values { get; } = [];

        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>(Values);

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken)
        {
            Values.Add(versionOverride);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeBackgroundTaskStore(Result<IReadOnlyList<OperationSnapshot>> result) : IBackgroundTaskStore
    {
        public int CallCount { get; private set; }

        public Task<Result<IReadOnlyList<OperationSnapshot>>> GetActiveAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }

        public Task<Result<Unit>> SaveAsync(BackgroundTaskRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeRenameJournal : IVersionRenameJournal
    {
        public Task<Result<Unit>> WriteAsync(VersionRenameJournalEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<VersionRenameJournalEntry?>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<VersionRenameJournalEntry?>.Success(null));

        public Task<Result<Unit>> DeleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
