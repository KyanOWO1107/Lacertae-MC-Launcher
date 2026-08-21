using System.Globalization;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Install;
using Lacertae.Application.Settings;
using Lacertae.Desktop.ViewModels.Downloads;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

namespace Lacertae.Desktop.Tests.Downloads;

public sealed class VanillaDownloadsViewModelTests
{
    [Fact]
    public async Task LoadAsyncFiltersOfficialVersionsByTypeAndSearch()
    {
        VanillaVersionSummary[] versions =
        [
            new("1.21.1", "release", DateTimeOffset.Parse("2024-09-19T00:00:00Z", CultureInfo.InvariantCulture), new Uri("https://piston-meta.mojang.com/release"), "0123456789abcdef0123456789abcdef01234567"),
            new("24w01a", "snapshot", DateTimeOffset.Parse("2024-01-04T00:00:00Z", CultureInfo.InvariantCulture), new Uri("https://launchermeta.mojang.com/snapshot"), "abcdef0123456789abcdef0123456789abcdef01"),
            new("1.20.6", "release", DateTimeOffset.Parse("2024-04-29T00:00:00Z", CultureInfo.InvariantCulture), new Uri("https://piston-meta.mojang.com/release-older"), "00112233445566778899aabbccddeeff00112233"),
        ];
        VanillaDownloadsViewModel viewModel = new(new FakeCatalog(versions));

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["1.21.1", "1.20.6", "24w01a"], viewModel.Versions.Select(static item => item.Id));
        Assert.Equal("official", viewModel.Versions.Single(static item => item.Id == "24w01a").SourceLabel);
        viewModel.TypeFilter = VanillaVersionTypeFilter.Release;
        Assert.Equal(["1.21.1", "1.20.6"], viewModel.FilteredVersions.Select(static item => item.Id));
        viewModel.SearchText = "20.6";
        Assert.Equal(["1.20.6"], viewModel.FilteredVersions.Select(static item => item.Id));
        Assert.All(viewModel.FilteredVersions, static item => Assert.Equal("official", item.SourceLabel));
    }

    [Fact]
    public async Task InstallStartsOnlyAfterExplicitConfirmation()
    {
        VanillaVersionSummary version = new(
            "1.21.1",
            "release",
            DateTimeOffset.UtcNow,
            new Uri("https://piston-meta.mojang.com/release"),
            "0123456789abcdef0123456789abcdef01234567");
        int startCount = 0;
        VanillaDownloadsViewModel viewModel = new(
            new FakeCatalog([version]),
            plan: (_, _, _) => Task.FromResult(Result<VanillaInstallPlan>.Success(
                new VanillaInstallPlan(
                    "operation",
                    InstallAction.Install,
                    "root",
                    "C:\\Games\\Minecraft",
                    "1.21.1",
                    "C:\\Games\\Minecraft\\versions\\1.21.1",
                    123456,
                    234567,
                    [],
                    DateTimeOffset.UtcNow))),
            start: (_, _, _) =>
            {
                startCount++;
                return Task.FromResult(Result.Success());
            });

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.SelectedVersion = viewModel.Versions[0];
        await viewModel.PrepareInstallAsync(
            new GameRoot("root", "C:\\Games\\Minecraft", "主游戏", GameRootAvailability.Available, null),
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsInstallConfirmationOpen);
        Assert.Equal("123456 B · 官方源", viewModel.InstallSummary);
        Assert.Equal(0, startCount);
        Assert.True(viewModel.ConfirmInstallCommand.CanExecute(null));

        viewModel.ConfirmInstallCommand.Execute(null);
        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(1, startCount);
        Assert.False(viewModel.IsInstallConfirmationOpen);
    }

    [Fact]
    public async Task CatalogFailureExposesStableErrorAndNoInstallAction()
    {
        VanillaDownloadsViewModel viewModel = new(new FakeCatalog(
            Result<IReadOnlyList<VanillaVersionSummary>>.Failure(new Problem(
                "VERSION_METADATA_UNAVAILABLE",
                ProblemStage.VersionResolution,
                "problem.version.metadata_unavailable",
                true,
                "test",
                ["action.version.retry_metadata"]))));

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.Equal("VERSION_METADATA_UNAVAILABLE", viewModel.ErrorCode);
        Assert.Empty(viewModel.Versions);
        Assert.False(viewModel.ConfirmInstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingRootPersistsSelectedRootId()
    {
        GameRoot first = Root("root-a", "A");
        GameRoot second = Root("root-b", "B");
        LauncherSettings initial = LauncherSettings.Default with { SelectedGameRootId = first.Id };
        FakeSettingsRepository settings = new(initial);
        using VanillaDownloadsViewModel viewModel = new(
            new FakeCatalog([]),
            gameRoots: [first, second],
            selectedRoot: first,
            settings: initial,
            settingsRepository: settings);

        viewModel.SelectedRoot = second;
        await settings.LastSave.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(second.Id, settings.LastSaved?.SelectedGameRootId);
    }

    [Fact]
    public async Task RootPersistenceFailureExposesActionableError()
    {
        GameRoot first = Root("root-a", "A");
        GameRoot second = Root("root-b", "B");
        LauncherSettings initial = LauncherSettings.Default with { SelectedGameRootId = first.Id };
        FakeSettingsRepository settings = new(
            initial,
            Result<Unit>.Failure(new Problem(
                "SETTINGS_SAVE_FAILED",
                ProblemStage.Configuration,
                "problem.settings.invalid",
                false,
                "test",
                [])));
        using VanillaDownloadsViewModel viewModel = new(
            new FakeCatalog([]),
            gameRoots: [first, second],
            selectedRoot: first,
            settings: initial,
            settingsRepository: settings);

        viewModel.SelectedRoot = second;
        await settings.LastSave.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.Equal("GAME_ROOT_SETTINGS_SAVE_FAILED", viewModel.ErrorCode);
        Assert.Contains("未能保存选择", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCommandRemainsAvailableAfterCatalogFailure()
    {
        CountingCatalog catalog = new(
            Result<IReadOnlyList<VanillaVersionSummary>>.Failure(new Problem(
                "VERSION_METADATA_UNAVAILABLE",
                ProblemStage.VersionResolution,
                "problem.version.metadata_unavailable",
                true,
                "test",
                [])));
        using VanillaDownloadsViewModel viewModel = new(catalog);

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.RefreshCommand.CanExecute(null));
        viewModel.RefreshCommand.Execute(null);
        await catalog.SecondCall.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, catalog.CallCount);
    }

    [Fact]
    public async Task LoadRefreshesRootListAndSelectedRootAfterOnboardingAddsRoot()
    {
        MutableRootRepository roots = new();
        FakeSettingsRepository settings = new(LauncherSettings.Default);
        using VanillaDownloadsViewModel viewModel = new(
            new FakeCatalog([]),
            settings: LauncherSettings.Default,
            settingsRepository: settings,
            gameRootRepository: roots);

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        GameRoot first = Root("root-a", "A");
        GameRoot second = Root("root-b", "B");
        roots.Values.Add(first);
        roots.Values.Add(second);
        settings.Current = LauncherSettings.Default with { SelectedGameRootId = second.Id };

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal([first.Id, second.Id], viewModel.GameRoots.Select(static root => root.Id));
        Assert.Equal(second.Id, viewModel.SelectedRoot?.Id);
    }

    private static GameRoot Root(string id, string displayName) =>
        new(id, Path.Combine(Path.GetTempPath(), "lacertae-downloads-" + id), displayName, GameRootAvailability.Available, null);

    private sealed class FakeCatalog : IVanillaVersionCatalog
    {
        private readonly Result<IReadOnlyList<VanillaVersionSummary>> result;

        public FakeCatalog(IReadOnlyList<VanillaVersionSummary> versions)
        {
            result = Result<IReadOnlyList<VanillaVersionSummary>>.Success(versions);
        }

        public FakeCatalog(Result<IReadOnlyList<VanillaVersionSummary>> result) => this.result = result;

        public Task<Result<IReadOnlyList<VanillaVersionSummary>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CountingCatalog : IVanillaVersionCatalog
    {
        private readonly Result<IReadOnlyList<VanillaVersionSummary>> result;

        public CountingCatalog(Result<IReadOnlyList<VanillaVersionSummary>> result) => this.result = result;

        public int CallCount { get; private set; }

        public TaskCompletionSource<bool> SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<IReadOnlyList<VanillaVersionSummary>>> ListAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount >= 2)
            {
                SecondCall.TrySetResult(true);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class MutableRootRepository : IGameRootRepository
    {
        public List<GameRoot> Values { get; } = [];

        public Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameRoot>>(Values.ToArray());

        public Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken) =>
            Task.FromResult(Values.FirstOrDefault(root => root.NormalizedPath == normalizedPath));

        public Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken)
        {
            Values.RemoveAll(root => root.Id == gameRoot.Id);
            Values.Add(gameRoot);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken)
        {
            Values.RemoveAll(root => root.Id == id);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeSettingsRepository : ISettingsRepository
    {
        private readonly Result<Unit> saveResult;
        public LauncherSettings Current { get; set; }

        public FakeSettingsRepository(LauncherSettings initial)
            : this(initial, Result.Success())
        {
        }

        public FakeSettingsRepository(LauncherSettings initial, Result<Unit> saveResult)
        {
            Current = initial;
            this.saveResult = saveResult;
        }

        public TaskCompletionSource<bool> LastSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LauncherSettings? LastSaved { get; private set; }

        public Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<LauncherSettings>.Success(Current));

        public Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken)
        {
            LastSaved = settings;
            Current = settings;
            LastSave.TrySetResult(true);
            return Task.FromResult(saveResult);
        }
    }
}
