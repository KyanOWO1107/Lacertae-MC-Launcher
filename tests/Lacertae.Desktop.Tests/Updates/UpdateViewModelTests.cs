using Lacertae.Application.Updates;
using Lacertae.Desktop.ViewModels.Updates;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Desktop.Tests.Updates;

public sealed class UpdateViewModelTests
{
    [Fact]
    public async Task AvailableUpdateNeedsExplicitDownloadConfirmationAndCanBeCancelled()
    {
        bool downloadCalled = false;
        UpdateViewModel viewModel = new(
            enabled: true,
            check: _ => Task.FromResult(Result<UpdateCheckResult>.Success(new UpdateCheckResult(
                UpdateCheckStatus.Available,
                Verified(),
                null))),
            download: (_, _) =>
            {
                downloadCalled = true;
                return Task.FromResult(Result<StagedUpdate>.Success(new StagedUpdate("1.2.0", "staging/x", "updates.json")));
            });

        await viewModel.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateUiState.Available, viewModel.State);
        Assert.True(viewModel.OpenDownloadConfirmationCommand.CanExecute(null));
        viewModel.OpenDownloadConfirmationCommand.Execute(null);
        Assert.True(viewModel.IsConfirmationOpen);
        Assert.True(viewModel.ConfirmDownloadCommand.CanExecute(null));
        viewModel.CancelConfirmationCommand.Execute(null);
        Assert.False(viewModel.IsConfirmationOpen);
        Assert.False(downloadCalled);
    }

    [Fact]
    public async Task ActiveGamePreventsApplyAndSuccessfulApplyReturnsCurrent()
    {
        bool gameRunning = true;
        UpdateViewModel viewModel = new(
            enabled: true,
            check: _ => Task.FromResult(Result<UpdateCheckResult>.Success(new UpdateCheckResult(
                UpdateCheckStatus.Available,
                Verified(),
                null))),
            download: (_, _) => Task.FromResult(Result<StagedUpdate>.Success(new StagedUpdate("1.2.0", "staging/x", "updates.json"))),
            apply: (_, _) => Task.FromResult(Result<Unit>.Success(Unit.Value)),
            gameRunning: () => gameRunning);

        await viewModel.CheckAsync(TestContext.Current.CancellationToken);
        viewModel.OpenDownloadConfirmationCommand.Execute(null);
        await viewModel.ConfirmDownloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateUiState.ReadyToApply, viewModel.State);
        Assert.False(viewModel.ApplyOnExitCommand.CanExecute(null));
        viewModel.ApplyOnExitCommand.Execute(null);
        Assert.Equal("UPDATE_ACTIVE_OPERATION", viewModel.ErrorCode);

        gameRunning = false;
        Assert.True(viewModel.ApplyOnExitCommand.CanExecute(null));
        await viewModel.ApplyOnExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateUiState.Current, viewModel.State);
    }

    private static VerifiedUpdateManifest Verified() => new(
        new UpdateManifest(
            1,
            "test-key",
            UpdateChannel.Test,
            "1.2.0",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "1.0.0",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["zh-CN"] = "测试更新" },
            new Uri("https://updates.example.test/notes"),
            new UpdatePackage(
                "win-x64",
                new Uri("https://updates.example.test/package.zip"),
                128,
                new string('a', 64),
                new string('b', 64))),
        [1],
        [2]);
}
