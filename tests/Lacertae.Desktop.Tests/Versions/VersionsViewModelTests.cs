using Lacertae.Application.GameRoots;
using Lacertae.Application.Games;
using Lacertae.Application.Platform;
using Lacertae.Application.Settings;
using Lacertae.Application.Versions;
using Lacertae.Desktop.ViewModels.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Desktop.Tests.Versions;

public sealed class VersionsViewModelTests
{
    [Fact]
    public async Task LoadSelectsConfiguredRootAndSortsFilteredVersions()
    {
        GameRoot first = Root("root-a", "A", GameRootAvailability.Available);
        GameRoot second = Root("root-b", "B", GameRootAvailability.Available);
        FakeRootRepository roots = new([first, second]);
        FakeGameEngine engine = new([
            Descriptor("root-b", "z-folder", "Zulu", "release", false),
            Descriptor("root-b", "a-folder", "Alpha", "snapshot", true),
            Descriptor("root-b", "b-folder", "Alpha", "release", false),
        ]);
        ListGameVersions list = new(engine, new FakeOverrideRepository());
        VersionsViewModel viewModel = new(
            roots,
            list,
            LauncherSettings.Default with { SelectedGameRootId = second.Id });

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(second.Id, viewModel.SelectedGameRoot?.Id);
        Assert.Equal(["a-folder", "b-folder", "z-folder"], viewModel.VisibleVersions.Select(static row => row.FolderName));
        Assert.Equal("Alpha", viewModel.VisibleVersions[0].DisplayName);
        viewModel.SearchText = "zulu";
        Assert.Equal(["z-folder"], viewModel.VisibleVersions.Select(static row => row.FolderName));
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task UnavailableRootDoesNotInspectVersionsAndExposesTypedState()
    {
        GameRoot unavailable = Root("root-a", "Missing", GameRootAvailability.Unavailable);
        FakeGameEngine engine = new([]);
        VersionsViewModel viewModel = new(
            new FakeRootRepository([unavailable]),
            new ListGameVersions(engine, new FakeOverrideRepository()),
            LauncherSettings.Default);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(viewModel.IsRootUnavailable);
        Assert.True(viewModel.HasError);
        Assert.Equal("GAME_ROOT_UNAVAILABLE", viewModel.ErrorCode);
        Assert.Empty(viewModel.Versions);
        Assert.Equal(0, engine.InspectCount);
    }

    [Fact]
    public async Task RootSwitchClearsStaleRowsBeforeLoadingNewRoot()
    {
        GameRoot first = Root("root-a", "A", GameRootAvailability.Available);
        GameRoot second = Root("root-b", "B", GameRootAvailability.Available);
        FakeGameEngine engine = new([
            Descriptor("root-a", "first", "First", "release", false),
            Descriptor("root-b", "second", "Second", "release", false),
        ]);
        VersionsViewModel viewModel = new(
            new FakeRootRepository([first, second]),
            new ListGameVersions(engine, new FakeOverrideRepository()),
            LauncherSettings.Default);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.SelectGameRootAsync(second.Id, CancellationToken.None);

        Assert.Equal(["second"], viewModel.Versions.Select(static row => row.FolderName));
        Assert.DoesNotContain(viewModel.Versions, row => row.FolderName == "first");
    }

    [Fact]
    public async Task RootSwitchPersistsSelectedRootId()
    {
        GameRoot first = Root("root-a", "A", GameRootAvailability.Available);
        GameRoot second = Root("root-b", "B", GameRootAvailability.Available);
        FakeSettingsRepository settings = new();
        VersionsViewModel viewModel = new(
            new FakeRootRepository([first, second]),
            new ListGameVersions(
                new FakeGameEngine([Descriptor("root-b", "second", "Second", "release", false)]),
                new FakeOverrideRepository()),
            LauncherSettings.Default,
            settingsRepository: settings);

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SelectGameRootAsync(second.Id, CancellationToken.None);

        Assert.Equal(second.Id, settings.LastSaved?.SelectedGameRootId);
    }

    [Fact]
    public async Task OlderRefreshCannotReplaceRowsAfterRootSwitch()
    {
        GameRoot first = Root("root-a", "A", GameRootAvailability.Available);
        GameRoot second = Root("root-b", "B", GameRootAvailability.Available);
        RacingGameEngine engine = new([
            Descriptor("root-a", "first", "First", "release", false),
            Descriptor("root-b", "second", "Second", "release", false),
        ]);
        VersionsViewModel viewModel = new(
            new FakeRootRepository([first, second]),
            new ListGameVersions(engine, new FakeOverrideRepository()),
            LauncherSettings.Default);

        await viewModel.LoadAsync(CancellationToken.None);
        engine.BlockNextRefresh();
        Task<Result<IReadOnlyList<ListedGameVersion>>> olderRefresh =
            viewModel.RefreshVersionsAsync(CancellationToken.None);
        await engine.FirstRefreshStarted.Task;

        await viewModel.SelectGameRootAsync(second.Id, CancellationToken.None);
        engine.ReleaseFirstRefresh.TrySetResult(true);
        await olderRefresh;

        Assert.Equal(["second"], viewModel.Versions.Select(static row => row.FolderName));
    }

    [Fact]
    public async Task OpenDirectoryUsesOnlyLoadedVersionPath()
    {
        GameRoot root = Root("root", "Root", GameRootAvailability.Available);
        FakeDialogService dialogs = new();
        VersionsViewModel viewModel = new(
            new FakeRootRepository([root]),
            new ListGameVersions(
                new FakeGameEngine([Descriptor("root", "1.21.1", "1.21.1", "release", false)]),
                new FakeOverrideRepository()),
            LauncherSettings.Default,
            dialogs);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.OpenDirectory(viewModel.Versions[0]);

        Assert.Equal(Path.GetFullPath(Path.Combine(root.NormalizedPath, "versions", "1.21.1")), dialogs.LastPath);
    }

    private static GameRoot Root(string id, string name, GameRootAvailability availability) =>
        new(id, Path.Combine(Path.GetTempPath(), "lacertae-" + id), name, availability, null);

    private static GameVersionDescriptor Descriptor(string rootId, string folder, string displayName, string type, bool loader) =>
        new(rootId, folder, displayName, type, null, new JavaRequirement("Minecraft", 21), loader);

    private sealed class FakeRootRepository(IReadOnlyList<GameRoot> values) : IGameRootRepository
    {
        public Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(values);

        public Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken) =>
            Task.FromResult(values.FirstOrDefault(root => root.NormalizedPath == normalizedPath));

        public Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeGameEngine(IReadOnlyList<GameVersionDescriptor> descriptors) : IGameEngine
    {
        public int InspectCount { get; private set; }

        public Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
            string gameRootPath,
            CancellationToken cancellationToken)
        {
            InspectCount++;
            string rootId = string.Empty;
            foreach (GameVersionDescriptor descriptor in descriptors)
            {
                if (gameRootPath.Contains(descriptor.GameRootId, StringComparison.Ordinal))
                {
                    rootId = descriptor.GameRootId;
                    break;
                }
            }
            return Task.FromResult(Result<IReadOnlyList<GameVersionDescriptor>>.Success(
                descriptors.Where(descriptor => descriptor.GameRootId == rootId).ToArray()));
        }

        public Task<Result<GameProcessSpec>> BuildProcessSpecAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GameProcessSpec>.Failure(new Lacertae.Domain.Problems.Problem(
                "UNSUPPORTED", Lacertae.Domain.Problems.ProblemStage.Process, "problem.unsupported", false, "test", [])));
    }

    private sealed class RacingGameEngine(IReadOnlyList<GameVersionDescriptor> descriptors) : IGameEngine
    {
        private int blockNext;

        public TaskCompletionSource<bool> FirstRefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextRefresh() => Interlocked.Exchange(ref blockNext, 1);

        public async Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
            string gameRootPath,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                FirstRefreshStarted.TrySetResult(true);
                await ReleaseFirstRefresh.Task.WaitAsync(cancellationToken);
            }

            string rootId = descriptors.FirstOrDefault(descriptor =>
                gameRootPath.Contains(descriptor.GameRootId, StringComparison.Ordinal))?.GameRootId ?? string.Empty;
            return Result<IReadOnlyList<GameVersionDescriptor>>.Success(
                descriptors.Where(descriptor => descriptor.GameRootId == rootId).ToArray());
        }

        public Task<Result<GameProcessSpec>> BuildProcessSpecAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GameProcessSpec>.Failure(new Lacertae.Domain.Problems.Problem(
                "UNSUPPORTED", Lacertae.Domain.Problems.ProblemStage.Process, "problem.unsupported", false, "test", [])));
    }

    private sealed class FakeOverrideRepository : IVersionOverrideRepository
    {
        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>([]);

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeSettingsRepository : ISettingsRepository
    {
        public LauncherSettings? LastSaved { get; private set; }

        public Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<LauncherSettings>.Success(LauncherSettings.Default));

        public Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken)
        {
            LastSaved = settings;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeDialogService : IPlatformDialogService
    {
        public string? LastPath { get; private set; }

        public void OpenDirectory(string normalizedPath) => LastPath = normalizedPath;
    }
}
