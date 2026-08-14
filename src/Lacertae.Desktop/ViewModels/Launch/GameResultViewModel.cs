using System.Collections.ObjectModel;
using System.ComponentModel;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;

namespace Lacertae.Desktop.ViewModels.Launch;

public enum GameResultKind
{
    NormalExit,
    ExplicitStop,
    StartFailed,
    AbnormalExit,
}

public sealed class GameResultViewModel : INotifyPropertyChanged
{
    public GameResultViewModel(GameExitResult exit, Problem? problem = null, GameCrashReport? crashReport = null)
    {
        Exit = exit ?? throw new ArgumentNullException(nameof(exit));
        Problem = problem;
        CrashReport = crashReport;
        Kind = MapKind(exit, problem);
        Findings = new ObservableCollection<DiagnosticFinding>((crashReport?.Findings ?? []).OrderBy(static finding => finding.Confidence));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public GameExitResult Exit { get; }
    public Problem? Problem { get; }
    public GameCrashReport? CrashReport { get; }
    public GameResultKind Kind { get; }
    public string Outcome => Kind switch
    {
        GameResultKind.NormalExit => "正常退出",
        GameResultKind.ExplicitStop => "已停止",
        GameResultKind.StartFailed => "启动失败",
        _ => "异常退出",
    };
    public bool IsNormalExit => Kind == GameResultKind.NormalExit;
    public bool IsExplicitStop => Kind == GameResultKind.ExplicitStop;
    public bool IsStartFailed => Kind == GameResultKind.StartFailed;
    public bool IsAbnormalExit => Kind == GameResultKind.AbnormalExit;
    public int? ExitCode => Exit.ExitCode;
    public string CorrelationId => Exit.CorrelationId;
    public IReadOnlyList<DiagnosticFinding> Findings { get; }
    public bool HasFindings => Findings.Count > 0;
    public bool IsTechnicalDetailsExpanded { get; private set; }

    public static GameResultViewModel From(GameExitResult exit, Problem? problem = null, GameCrashReport? crashReport = null) =>
        new(exit, problem, crashReport);

    public void ToggleTechnicalDetails()
    {
        IsTechnicalDetailsExpanded = !IsTechnicalDetailsExpanded;
        PropertyChanged?.Invoke(this, new(nameof(IsTechnicalDetailsExpanded)));
    }

    private static GameResultKind MapKind(GameExitResult exit, Problem? problem) =>
        exit.State == GameProcessState.UserTerminated ? GameResultKind.ExplicitStop :
        exit.State == GameProcessState.StartFailed || string.Equals(problem?.Code, "PROCESS_START_FAILED", StringComparison.Ordinal) ? GameResultKind.StartFailed :
        exit.State == GameProcessState.Exited && exit.ExitCode == 0 ? GameResultKind.NormalExit :
        GameResultKind.AbnormalExit;
}
