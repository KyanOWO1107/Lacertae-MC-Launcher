using Lacertae.Domain.Accounts;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Launch;
using Lacertae.Infrastructure.Diagnostics;

namespace Lacertae.Infrastructure.Tests.Diagnostics;

public sealed class GameCrashAnalyzerTests
{
    [Theory]
    [InlineData("java-oom.log", "JAVA_OOM")]
    [InlineData("unsupported-class.log", "JAVA_CLASS_VERSION_UNSUPPORTED")]
    [InlineData("missing-main.log", "MINECRAFT_MAIN_CLASS_MISSING")]
    [InlineData("access-denied.log", "PATH_ACCESS_DENIED")]
    public async Task AnalyzeAsyncReportsOnlyEvidenceBackedFinding(string fixture, string code)
    {
        string root = CreateTemporaryDirectory();
        string logPath = Path.Combine(root, "launch.log");
        File.Copy(Fixture(fixture), logPath);
        LaunchPlan plan = CreatePlan(root);
        GameExitResult exit = new(123, 1, GameProcessState.Exited, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, plan.CorrelationId);

        var result = await new GameCrashAnalyzer().AnalyzeAsync(plan, exit, logPath, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        DiagnosticFinding finding = Assert.Single(result.Value.Findings);
        Assert.Equal(code, finding.Code);
        Assert.Equal(DiagnosticConfidence.Confirmed, finding.Confidence);
        Assert.NotEmpty(finding.EvidenceLineNumbers);
        Assert.DoesNotContain("access-token-secret", finding.MessageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsyncReportsNativeCrashFileAndDoesNotGuessUnknownExit()
    {
        string root = CreateTemporaryDirectory();
        string logPath = Path.Combine(root, "launch.log");
        File.WriteAllText(logPath, File.ReadAllText(Fixture("unknown-exit.log")));
        File.WriteAllText(Path.Combine(root, "hs_err_pid123.log"), "# A fatal error has been detected by the Java Runtime Environment");
        LaunchPlan plan = CreatePlan(root);
        GameExitResult exit = new(123, 9, GameProcessState.Exited, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, plan.CorrelationId);

        var result = await new GameCrashAnalyzer().AnalyzeAsync(plan, exit, logPath, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Contains(result.Value.Findings, finding => finding.Code == "NATIVE_CRASH");
        Assert.DoesNotContain(result.Value.Findings, finding => finding.Code == "GAME_ABNORMAL_EXIT");
        Assert.All(result.Value.Findings, finding => Assert.All(finding.EvidenceLineNumbers, line => Assert.True(line >= 1)));
    }

    [Fact]
    public async Task AnalyzeAsyncKeepsUnknownNonzeroExitTransparent()
    {
        string root = CreateTemporaryDirectory();
        string logPath = Path.Combine(root, "launch.log");
        File.Copy(Fixture("unknown-exit.log"), logPath);
        LaunchPlan plan = CreatePlan(root);
        GameExitResult exit = new(123, 37, GameProcessState.Exited, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, plan.CorrelationId);

        var result = await new GameCrashAnalyzer().AnalyzeAsync(plan, exit, logPath, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        DiagnosticFinding finding = Assert.Single(result.Value.Findings);
        Assert.Equal("GAME_ABNORMAL_EXIT", finding.Code);
        Assert.Equal(DiagnosticConfidence.Unknown, finding.Confidence);
        Assert.Contains("action.diagnostics.open_log", finding.SuggestedActionKeys);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Diagnostics", "Fixtures", name);

    private static LaunchPlan CreatePlan(string root) => new(
        "corr-diagnostic",
        "root-1",
        "fixture",
        "fixture",
        root,
        root,
        "java-17",
        Path.Combine(root, "java.exe"),
        17,
        "account-1",
        AccountType.Offline,
        "Player",
        "5627dd98-e6be-3c21-b8a8-e92344183641",
        new AuthSession("Player", "5627dd98-e6be-3c21-b8a8-e92344183641", new SensitiveString("access-token-secret"), "legacy", null, null),
        1024,
        2048,
        [],
        [],
        [],
        [],
        LaunchDisposition.KeepLauncherOpen,
        DateTimeOffset.UtcNow);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
