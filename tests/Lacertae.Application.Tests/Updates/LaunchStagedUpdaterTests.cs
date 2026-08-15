using System.Text.Json;
using Lacertae.Application.Updates;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Updates;

public sealed class LaunchStagedUpdaterTests
{
    [Fact]
    public async Task ConfirmedLaunchWritesStrictPlanAndStartsUpdater()
    {
        using TemporaryRoot root = new();
        FakeStarter starter = new();
        LaunchStagedUpdater useCase = new(starter);
        LaunchStagedUpdaterRequest request = root.Request(confirmed: true, gameRunning: false, installRunning: false);

        var result = await useCase.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(result.Value, starter.PlanPath);
        Assert.Equal("--plan", starter.ArgumentName);
        using JsonDocument plan = JsonDocument.Parse(await File.ReadAllTextAsync(result.Value, TestContext.Current.CancellationToken));
        Assert.Equal(request.HealthNonce, plan.RootElement.GetProperty("healthNonce").GetString());
        Assert.Equal(request.NewExecutableRelativePath, plan.RootElement.GetProperty("newExecutableRelativePath").GetString());
    }

    [Fact]
    public async Task LaunchRequiresConfirmationAndRejectsActiveOperations()
    {
        using TemporaryRoot root = new();
        LaunchStagedUpdater useCase = new(new FakeStarter());

        var notConfirmed = await useCase.ExecuteAsync(
            root.Request(confirmed: false, gameRunning: false, installRunning: false),
            TestContext.Current.CancellationToken);
        var active = await useCase.ExecuteAsync(
            root.Request(confirmed: true, gameRunning: true, installRunning: false),
            TestContext.Current.CancellationToken);

        Assert.Equal("UPDATE_CONFIRMATION_REQUIRED", notConfirmed.Problem?.Code);
        Assert.Equal("UPDATE_ACTIVE_OPERATION", active.Problem?.Code);
    }

    private sealed class FakeStarter : IUpdaterProcessStarter
    {
        public string? PlanPath { get; private set; }

        public string? ArgumentName { get; private set; }

        public Result<Unit> Start(string updaterExecutablePath, string workingDirectory, string planPath)
        {
            PlanPath = planPath;
            ArgumentName = "--plan";
            return Result.Success();
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "lacertae-launch-update-" + Guid.NewGuid().ToString("N"));
            UpdatesPath = Path.Combine(Root, "updates");
            InstallPath = Path.Combine(Root, "install");
            StagingPath = Path.Combine(Root, "staging");
            BackupPath = Path.Combine(Root, "backup");
            Directory.CreateDirectory(UpdatesPath);
            Directory.CreateDirectory(InstallPath);
            Directory.CreateDirectory(StagingPath);
            UpdaterPath = Path.Combine(Root, "updater.exe");
            File.WriteAllText(UpdaterPath, "stub");
        }

        private string Root { get; }
        private string UpdatesPath { get; }
        private string InstallPath { get; }
        private string StagingPath { get; }
        private string BackupPath { get; }
        private string UpdaterPath { get; }

        public LaunchStagedUpdaterRequest Request(bool confirmed, bool gameRunning, bool installRunning) => new(
            UpdaterPath,
            UpdatesPath,
            InstallPath,
            StagingPath,
            BackupPath,
            "app.exe",
            "nonce-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(1),
            ["old.exe"],
            ["app.exe"],
            confirmed,
            gameRunning,
            installRunning,
            "launch-test");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
