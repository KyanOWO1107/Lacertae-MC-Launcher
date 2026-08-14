using System.ComponentModel;
using Lacertae.Domain.Operations;

namespace Lacertae.Desktop.ViewModels.Tasks;

public sealed class TaskItemViewModel : INotifyPropertyChanged
{
    public TaskItemViewModel(OperationSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public OperationSnapshot Snapshot { get; private set; }
    public string Id => Snapshot.Id;
    public string Kind => Snapshot.Kind;
    public string SourceId => Snapshot.Id;
    public string CorrelationId => Snapshot.Id;
    public OperationState State => Snapshot.State;
    public string StateLabel => State switch
    {
        OperationState.Pending => "等待中",
        OperationState.Running => "进行中",
        OperationState.Succeeded => "已完成",
        OperationState.Failed => "失败",
        OperationState.Cancelled => "已取消",
        _ => State.ToString(),
    };
    public string Stage => Snapshot.Progress?.Stage ?? "等待中";
    public long CompletedItems => Snapshot.Progress?.CompletedItems ?? 0;
    public long TotalItems => Snapshot.Progress?.TotalItems ?? 0;
    public long CompletedBytes => Snapshot.Progress?.CompletedBytes ?? 0;
    public long TotalBytes => Snapshot.Progress?.TotalBytes ?? 0;
    public double Progress => Snapshot.Progress is { } progress && progress.TotalItems > 0
        ? Math.Clamp((double)progress.CompletedItems / progress.TotalItems, 0, 1)
        : Snapshot.Progress is { TotalBytes: > 0 } bytes
            ? Math.Clamp((double)bytes.CompletedBytes / bytes.TotalBytes, 0, 1)
            : State == OperationState.Succeeded ? 1 : 0;
    public string ProgressLabel => $"{Progress:P0}";
    public string ProblemCode => Snapshot.ProblemCode ?? string.Empty;
    public bool HasProblem => Snapshot.ProblemCode is not null;
    public bool IsRetryableProblem => HasProblem && Snapshot.ProblemCode != "OPERATION_CANCELLED";
    public bool IsTerminal => State is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled;
    public bool RetrySupported { get; internal set; }
    public bool CanRetry => RetrySupported && State == OperationState.Failed && IsRetryableProblem;
    public bool CanCancel => State is (OperationState.Pending or OperationState.Running) && !IsAtomicCommitOrProcessStart;
    public bool IsAtomicCommitOrProcessStart =>
        string.Equals(Stage, "commit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Stage, "atomic-commit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Stage, "launch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Stage, "process-start", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Stage, "starting", StringComparison.OrdinalIgnoreCase);
    public double BytesPerSecond { get; private set; }

    public void Apply(OperationSnapshot snapshot, double bytesPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.Id, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Snapshot id must remain stable.", nameof(snapshot));
        }

        Snapshot = snapshot;
        BytesPerSecond = Math.Max(0, bytesPerSecond);
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(CompletedItems));
        OnPropertyChanged(nameof(TotalItems));
        OnPropertyChanged(nameof(CompletedBytes));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProblemCode));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsRetryableProblem));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsAtomicCommitOrProcessStart));
        OnPropertyChanged(nameof(BytesPerSecond));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
