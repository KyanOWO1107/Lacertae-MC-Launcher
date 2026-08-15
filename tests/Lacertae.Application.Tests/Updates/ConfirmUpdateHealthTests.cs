using System.Text.Json;
using Lacertae.Application.Updates;

namespace Lacertae.Application.Tests.Updates;

public sealed class ConfirmUpdateHealthTests
{
    [Fact]
    public async Task HealthIsWrittenOnlyAfterStartupAndFirstRender()
    {
        string root = CreateTemporaryDirectory();
        string nonce = "nonce-" + Guid.NewGuid().ToString("N");
        ConfirmUpdateHealth useCase = new();

        var notReady = await useCase.ExecuteAsync(
            new ConfirmUpdateHealthRequest(root, nonce, Environment.ProcessId, true, false, "health-test"),
            TestContext.Current.CancellationToken);
        Assert.False(notReady.IsSuccess);
        Assert.Equal("UPDATE_HEALTH_NOT_READY", notReady.Problem?.Code);

        var ready = await useCase.ExecuteAsync(
            new ConfirmUpdateHealthRequest(root, nonce, Environment.ProcessId, true, true, "health-test"),
            TestContext.Current.CancellationToken);

        Assert.True(ready.IsSuccess, ready.Problem?.Code);
        string path = ConfirmUpdateHealth.GetHealthFilePath(root, nonce);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(nonce, document.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(Environment.ProcessId, document.RootElement.GetProperty("processId").GetInt32());
    }

    [Fact]
    public async Task HealthRejectsInvalidNonceAndPath()
    {
        ConfirmUpdateHealth useCase = new();

        var result = await useCase.ExecuteAsync(
            new ConfirmUpdateHealthRequest("relative", "bad", Environment.ProcessId, true, true, "health-test"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("UPDATE_HEALTH_INVALID", result.Problem?.Code);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-health-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
