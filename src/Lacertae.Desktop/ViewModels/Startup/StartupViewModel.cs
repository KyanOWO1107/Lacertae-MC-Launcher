using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Lacertae.Domain.Problems;

namespace Lacertae.Desktop.ViewModels.Startup;

public sealed record StartupProgressSnapshot
{
    public StartupProgressSnapshot(string stageKey, double fraction)
    {
        StageKey = string.IsNullOrWhiteSpace(stageKey)
            ? throw new ArgumentException("Stage key cannot be blank.", nameof(stageKey))
            : stageKey;
        Fraction = Math.Clamp(fraction, 0, 1);
    }

    public string StageKey { get; }

    public double Fraction { get; }
}

public interface IStartupRecoveryHost
{
    bool CanRetry { get; }

    bool CanRestore { get; }

    bool CanOpenLog { get; }

    Task RetryAsync(CancellationToken cancellationToken);

    Task RestoreAsync(CancellationToken cancellationToken);

    Task OpenLogAsync(CancellationToken cancellationToken);
}

public sealed class StartupViewModel
{
    private const string UnknownSummary = "启动初始化失败，请查看问题代码和日志。";

    private static readonly Dictionary<string, string> SummaryByMessageKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["problem.startup.failed"] = "启动器无法完成初始化。",
            ["problem.settings.invalid"] = "设置文件无法读取或已损坏。",
            ["problem.data_root.unwritable"] = "启动器数据目录不可写。",
        };

    private readonly IStartupRecoveryHost recoveryHost;

    public StartupViewModel(
        Problem problem,
        StartupProgressSnapshot? progress = null,
        IStartupRecoveryHost? recoveryHost = null)
    {
        Problem = problem ?? throw new ArgumentNullException(nameof(problem));
        Progress = progress ?? new StartupProgressSnapshot("startup", 0);
        this.recoveryHost = recoveryHost ?? DisabledStartupRecoveryHost.Instance;
        RetryCommand = new AsyncRelayCommand(
            () => RetryAsync(CancellationToken.None),
            () => CanRetry);
        RestoreCommand = new AsyncRelayCommand(
            () => RestoreAsync(CancellationToken.None),
            () => CanRestore);
        OpenLogCommand = new AsyncRelayCommand(
            () => OpenLogAsync(CancellationToken.None),
            () => CanOpenLog);
    }

    public Problem Problem { get; }

    public StartupProgressSnapshot Progress { get; }

    public string ProblemCode => Problem.Code;

    public string LocalizedSummary => Problem.SafeContext.TryGetValue("summary", out string? summary) &&
            !string.IsNullOrWhiteSpace(summary)
        ? summary
        : SummaryByMessageKey.TryGetValue(Problem.MessageKey, out string? localized)
            ? localized
            : UnknownSummary;

    public string? SafePath => Problem.SafeContext.TryGetValue("safePath", out string? path) &&
            !string.IsNullOrWhiteSpace(path)
        ? path
        : null;

    public string SafePathLabel => SafePath is null ? string.Empty : $"安全路径：{SafePath}";

    public bool CanRetry => recoveryHost.CanRetry && (Problem.IsRetryable || HasAction("retry"));

    public bool CanRestore => recoveryHost.CanRestore && HasAction("restore");

    public bool CanOpenLog => recoveryHost.CanOpenLog && HasAction("log");

    public ICommand RetryCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand OpenLogCommand { get; }

    public Task RetryAsync(CancellationToken cancellationToken) =>
        CanRetry
            ? recoveryHost.RetryAsync(cancellationToken)
            : Task.CompletedTask;

    public Task RestoreAsync(CancellationToken cancellationToken) =>
        CanRestore
            ? recoveryHost.RestoreAsync(cancellationToken)
            : Task.CompletedTask;

    public Task OpenLogAsync(CancellationToken cancellationToken) =>
        CanOpenLog
            ? recoveryHost.OpenLogAsync(cancellationToken)
            : Task.CompletedTask;

    private bool HasAction(string fragment) => Problem.SuggestedActionKeys.Any(action =>
        action.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private sealed class DisabledStartupRecoveryHost : IStartupRecoveryHost
    {
        public static DisabledStartupRecoveryHost Instance { get; } = new();

        public bool CanRetry => false;

        public bool CanRestore => false;

        public bool CanOpenLog => false;

        public Task RetryAsync(CancellationToken cancellationToken) => Unavailable();

        public Task RestoreAsync(CancellationToken cancellationToken) => Unavailable();

        public Task OpenLogAsync(CancellationToken cancellationToken) => Unavailable();

        private static Task Unavailable() => Task.FromException(
            new NotSupportedException("Startup recovery action is unavailable."));
    }
}
