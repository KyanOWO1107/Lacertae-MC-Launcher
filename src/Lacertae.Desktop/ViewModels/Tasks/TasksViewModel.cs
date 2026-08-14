using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Operations;
using Lacertae.Domain.Operations;

namespace Lacertae.Desktop.ViewModels.Tasks;

public sealed class TasksViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IBackgroundTaskStore? taskStore;
    private readonly Func<TaskItemViewModel, Task>? retry;
    private readonly Func<TaskItemViewModel, Task>? cancel;
    private readonly Dictionary<string, DateTimeOffset> lastRender = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, long Bytes)> throughput = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationSnapshot> pending = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Timer flushTimer;
    private string? errorCode;

    public TasksViewModel(
        IEnumerable<OperationSnapshot>? snapshots = null,
        IBackgroundTaskStore? taskStore = null,
        Func<TaskItemViewModel, Task>? retry = null,
        Func<TaskItemViewModel, Task>? cancel = null)
    {
        this.taskStore = taskStore;
        this.retry = retry;
        this.cancel = cancel;
        flushTimer = new Timer(_ => FlushPending(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        if (snapshots is not null)
        {
            foreach (OperationSnapshot snapshot in snapshots)
            {
                ApplySnapshot(snapshot, force: true);
            }
        }
        ClearCompletedCommand = new DelegateCommand(_ => ClearCompleted());
        RetryCommand = new AsyncCommand<TaskItemViewModel>(RetryAsync, item => item?.CanRetry == true && retry is not null);
        CancelCommand = new AsyncCommand<TaskItemViewModel>(CancelAsync, item => item?.CanCancel == true && cancel is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<TaskItemViewModel> Items { get; } = [];
    public IReadOnlyList<TaskItemViewModel> Tasks => Items;
    public ICommand ClearCompletedCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelCommand { get; }
    public string? ErrorCode { get => errorCode; private set { if (errorCode != value) { errorCode = value; PropertyChanged?.Invoke(this, new(nameof(ErrorCode))); } } }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (taskStore is null) return;
        Lacertae.Domain.Results.Result<IReadOnlyList<OperationSnapshot>> result = await taskStore.GetActiveAsync(cancellationToken);
        if (!result.IsSuccess) { ErrorCode = result.Problem?.Code; return; }
        foreach (OperationSnapshot snapshot in result.Value) ApplySnapshot(snapshot, force: true);
    }

    public void AcceptSnapshot(OperationSnapshot snapshot) => ApplySnapshot(snapshot, force: false);
    public void OnSnapshot(OperationSnapshot snapshot) => AcceptSnapshot(snapshot);
    public void ApplySnapshot(OperationSnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TaskItemViewModel? existing = Items.FirstOrDefault(item => item.Id == snapshot.Id);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool terminal = snapshot.State is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled;
        lock (gate)
        {
            if (!force && !terminal && lastRender.TryGetValue(snapshot.Id, out DateTimeOffset previous) && now - previous < TimeSpan.FromMilliseconds(100))
            {
                pending[snapshot.Id] = snapshot;
                return;
            }
            lastRender[snapshot.Id] = now;
            pending.Remove(snapshot.Id);
        }
        double bytesPerSecond = 0;
        if (snapshot.Progress is { } progress)
        {
            lock (gate)
            {
                if (throughput.TryGetValue(snapshot.Id, out (DateTimeOffset At, long Bytes) previous))
                {
                    double seconds = (now - previous.At).TotalSeconds;
                    if (seconds > 0) bytesPerSecond = Math.Max(0, (progress.CompletedBytes - previous.Bytes) / seconds);
                }
                throughput[snapshot.Id] = (now, progress.CompletedBytes);
            }
        }
        if (existing is null)
        {
            existing = new TaskItemViewModel(snapshot) { RetrySupported = retry is not null };
            Items.Add(existing);
        }
        else existing.Apply(snapshot, bytesPerSecond);
        PropertyChanged?.Invoke(this, new(nameof(Items)));
    }

    public void ClearCompleted()
    {
        foreach (TaskItemViewModel item in Items.Where(static item => item.IsTerminal).ToArray()) Items.Remove(item);
    }

    private async Task RetryAsync(TaskItemViewModel? item) { if (item is not null && retry is not null) await retry(item); }
    private async Task CancelAsync(TaskItemViewModel? item) { if (item is not null && cancel is not null) await cancel(item); }
    public void Dispose() { flushTimer.Dispose(); lock (gate) { pending.Clear(); lastRender.Clear(); throughput.Clear(); } }

    private void FlushPending()
    {
        OperationSnapshot[] snapshots;
        lock (gate)
        {
            snapshots = pending.Values.ToArray();
            pending.Clear();
        }
        foreach (OperationSnapshot snapshot in snapshots)
        {
            ApplySnapshot(snapshot, force: true);
        }
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncCommand<T>(Func<T?, Task> execute, Func<T?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => canExecute(parameter is T value ? value : default);
        public async void Execute(object? parameter) => await execute(parameter is T value ? value : default);
    }
}
