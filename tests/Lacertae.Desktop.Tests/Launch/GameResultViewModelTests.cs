using Lacertae.Desktop.ViewModels.Launch;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Launch;

namespace Lacertae.Desktop.Tests.Launch;

public sealed class GameResultViewModelTests
{
    [Fact]
    public void ResultsDistinguishNormalStopAndAbnormalExit()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(new GameResultViewModel(new(null, 0, GameProcessState.Exited, now, now, "a")).IsNormalExit);
        Assert.True(new GameResultViewModel(new(null, null, GameProcessState.UserTerminated, now, now, "b")).IsExplicitStop);
        Assert.True(new GameResultViewModel(new(null, 2, GameProcessState.Exited, now, now, "c")).IsAbnormalExit);
    }

    [Fact]
    public void FindingsAreOrderedByConfidenceAndDetailsStartCollapsed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GameCrashReport report = new(1, [
            new DiagnosticFinding("u", DiagnosticConfidence.Unknown, "u", [], []),
            new DiagnosticFinding("c", DiagnosticConfidence.Confirmed, "c", [], []),
            new DiagnosticFinding("l", DiagnosticConfidence.Likely, "l", [], []),
        ], "log", "id");
        GameResultViewModel viewModel = new(new(null, 1, GameProcessState.Exited, now, now, "id"), crashReport: report);
        Assert.Equal([DiagnosticConfidence.Confirmed, DiagnosticConfidence.Likely, DiagnosticConfidence.Unknown], viewModel.Findings.Select(f => f.Confidence));
        Assert.False(viewModel.IsTechnicalDetailsExpanded);
        viewModel.ToggleTechnicalDetails();
        Assert.True(viewModel.IsTechnicalDetailsExpanded);
    }

    [Fact]
    public void StartFailureStateIsExposedSeparately()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GameResultViewModel viewModel = new(new(null, null, GameProcessState.StartFailed, now, now, "id"));
        Assert.True(viewModel.IsStartFailed);
        Assert.False(viewModel.IsAbnormalExit);
    }
}
