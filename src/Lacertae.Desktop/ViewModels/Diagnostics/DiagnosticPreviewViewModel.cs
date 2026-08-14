using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Diagnostics;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Diagnostics;

namespace Lacertae.Desktop.ViewModels.Diagnostics;

public sealed class DiagnosticPreviewEntryViewModel : INotifyPropertyChanged
{
    private readonly bool required;
    private bool isIncluded;

    internal DiagnosticPreviewEntryViewModel(
        DiagnosticBundleEntry entry,
        Action<DiagnosticPreviewEntryViewModel> changed)
    {
        LogicalName = entry.LogicalName;
        Size = entry.Size;
        Sha256 = entry.Sha256;
        RedactionSummary = entry.RedactionSummary;
        required = entry.LogicalName is "launcher-version.json" or "manifest.json";
        isIncluded = entry.IsIncluded || required;
        this.changed = changed;
    }

    private readonly Action<DiagnosticPreviewEntryViewModel> changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LogicalName { get; }

    public long Size { get; }

    public string Sha256 { get; }

    public string RedactionSummary { get; }

    public bool IsRequired => required;

    public bool IsIncluded
    {
        get => isIncluded;
        set
        {
            bool next = required || value;
            if (next == isIncluded)
            {
                return;
            }

            isIncluded = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
            changed(this);
        }
    }
}

/// <summary>
/// Presentation-only state for the local diagnostic preview. It delegates
/// preparation and archive writing to application/infrastructure services and
/// never reads files itself.
/// </summary>
public sealed class DiagnosticPreviewViewModel : INotifyPropertyChanged
{
    private readonly BuildDiagnosticBundle build;
    private readonly Func<string, ZipDiagnosticBundleWriter>? writerFactory;
    private PreparedDiagnosticBundle? prepared;
    private DiagnosticBundleRequest? request;
    private string? errorCode;
    private bool isPreparing;
    private bool isSaving;
    private bool isSaveConfirmationOpen;
    private string? pendingSavePath;

    public DiagnosticPreviewViewModel(
        BuildDiagnosticBundle build,
        ZipDiagnosticBundleWriter? writer = null)
    {
        this.build = build ?? throw new ArgumentNullException(nameof(build));
        if (writer is not null)
        {
            writerFactory = _ => writer;
        }

        PrepareCommand = new AsyncCommand(
            () => request is null ? Task.CompletedTask : PrepareAsync(request, CancellationToken.None),
            () => request is not null && !IsPreparing);
        SaveCommand = new AsyncCommand(
            () => pendingSavePath is null ? Task.CompletedTask : ConfirmSaveAsync(CancellationToken.None),
            () => IsPreviewReady && !IsSaving && IsSaveConfirmationOpen && pendingSavePath is not null);
        CancelSaveCommand = new DelegateCommand(_ => CancelSave(), () => IsSaveConfirmationOpen);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DiagnosticPreviewEntryViewModel> Items { get; } = [];

    public IReadOnlyList<DiagnosticPreviewEntryViewModel> Entries => Items;

    public DiagnosticBundleManifest? Manifest => prepared is null ? null : BuildCurrentManifest();

    public DiagnosticBundlePreparedHandle? PreparedHandle => prepared?.Handle;

    public bool IsPreviewReady => prepared is not null;

    public bool IsPreparing
    {
        get => isPreparing;
        private set => Set(ref isPreparing, value, nameof(IsPreparing));
    }

    public bool IsSaving
    {
        get => isSaving;
        private set => Set(ref isSaving, value, nameof(IsSaving));
    }

    public bool IsSaveConfirmationOpen
    {
        get => isSaveConfirmationOpen;
        private set => Set(ref isSaveConfirmationOpen, value, nameof(IsSaveConfirmationOpen));
    }

    public string? PendingSavePath
    {
        get => pendingSavePath;
        private set => Set(ref pendingSavePath, value, nameof(PendingSavePath));
    }

    public string? ErrorCode
    {
        get => errorCode;
        private set => Set(ref errorCode, value, nameof(ErrorCode));
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorCode);

    public ICommand PrepareCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelSaveCommand { get; }

    public async Task<Result<PreparedDiagnosticBundle>> PrepareAsync(
        DiagnosticBundleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.request = request;
        ErrorCode = null;
        IsPreparing = true;
        try
        {
            Result<PreparedDiagnosticBundle> result = await build.PrepareAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                SetError(result.Problem?.Code ?? "DIAGNOSTIC_BUNDLE_INVALID");
                prepared = null;
                Items.Clear();
                OnPropertyChanged(nameof(Manifest));
                OnPropertyChanged(nameof(PreparedHandle));
                OnPropertyChanged(nameof(IsPreviewReady));
                return result;
            }

            prepared = result.Value;
            Items.Clear();
            foreach (DiagnosticBundleEntry entry in result.Value.Manifest.Entries)
            {
                Items.Add(new DiagnosticPreviewEntryViewModel(entry, OnEntryChanged));
            }

            OnPropertyChanged(nameof(Manifest));
            OnPropertyChanged(nameof(PreparedHandle));
            OnPropertyChanged(nameof(IsPreviewReady));
            NotifyCommands();
            return result;
        }
        finally
        {
            IsPreparing = false;
            NotifyCommands();
        }
    }

    public void RequestSave(string destinationPath)
    {
        if (!IsPreviewReady)
        {
            SetError("DIAGNOSTIC_BUNDLE_PREVIEW_REQUIRED");
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            SetError("DIAGNOSTIC_BUNDLE_INVALID");
            return;
        }

        PendingSavePath = destinationPath;
        IsSaveConfirmationOpen = true;
        ErrorCode = null;
        NotifyCommands();
    }

    public async Task<Result<string>> SaveAsync(
        string destinationPath,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            SetError("DIAGNOSTIC_BUNDLE_CONFIRMATION_REQUIRED");
            return Failure<string>("DIAGNOSTIC_BUNDLE_CONFIRMATION_REQUIRED");
        }

        RequestSave(destinationPath);
        return await ConfirmSaveAsync(cancellationToken);
    }

    public async Task<Result<string>> ConfirmSaveAsync(CancellationToken cancellationToken)
    {
        if (!IsPreviewReady || prepared is null || string.IsNullOrWhiteSpace(PendingSavePath))
        {
            SetError("DIAGNOSTIC_BUNDLE_PREVIEW_REQUIRED");
            return Failure<string>("DIAGNOSTIC_BUNDLE_PREVIEW_REQUIRED");
        }

        IsSaving = true;
        ErrorCode = null;
        try
        {
            ZipDiagnosticBundleWriter? writer = writerFactory?.Invoke(request?.StagingDirectory ?? string.Empty) ??
                (request is null
                    ? null
                    : new ZipDiagnosticBundleWriter(
                        request.StagingDirectory ?? Path.Combine(Path.GetTempPath(), "Lacertae", "diagnostic-staging")));
            if (writer is null)
            {
                SetError("DIAGNOSTIC_BUNDLE_WRITER_UNAVAILABLE");
                return Failure<string>("DIAGNOSTIC_BUNDLE_WRITER_UNAVAILABLE");
            }

            Result<string> result = await writer.WriteAsync(
                prepared.Handle,
                BuildCurrentManifest(),
                PendingSavePath,
                confirmed: true,
                cancellationToken);
            if (!result.IsSuccess)
            {
                SetError(result.Problem?.Code ?? "DIAGNOSTIC_BUNDLE_WRITE_FAILED");
                return result;
            }

            CancelSave();
            return result;
        }
        finally
        {
            IsSaving = false;
            NotifyCommands();
        }
    }

    public void CancelSave()
    {
        PendingSavePath = null;
        IsSaveConfirmationOpen = false;
        NotifyCommands();
    }

    private DiagnosticBundleManifest BuildCurrentManifest()
    {
        DiagnosticBundleManifest manifest = prepared!.Manifest;
        DiagnosticBundleEntry[] entries = manifest.Entries
            .Select(entry =>
            {
                DiagnosticPreviewEntryViewModel item = Items.First(candidate =>
                    string.Equals(candidate.LogicalName, entry.LogicalName, StringComparison.Ordinal));
                return entry with { IsIncluded = item.IsIncluded || item.IsRequired };
            })
            .ToArray();
        manifest = manifest with { Entries = entries };
        DiagnosticBundleEntry? manifestEntry = entries.FirstOrDefault(entry =>
            string.Equals(entry.LogicalName, "manifest.json", StringComparison.Ordinal));
        if (manifestEntry is null)
        {
            return manifest;
        }

        long declaredSize = manifestEntry.Size;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            entries = entries
                .Select(entry => string.Equals(entry.LogicalName, "manifest.json", StringComparison.Ordinal)
                    ? entry with { Size = declaredSize }
                    : entry)
                .ToArray();
            manifest = manifest with { Entries = entries };
            long actualSize = BuildDiagnosticBundle.SerializeManifest(manifest).LongLength;
            if (actualSize == declaredSize)
            {
                break;
            }

            declaredSize = actualSize;
        }

        return manifest;
    }

    private void OnEntryChanged(DiagnosticPreviewEntryViewModel _)
    {
        OnPropertyChanged(nameof(Manifest));
    }

    private void SetError(string code)
    {
        ErrorCode = code;
        OnPropertyChanged(nameof(HasError));
    }

    private void NotifyCommands()
    {
        if (PrepareCommand is AsyncCommand prepare)
        {
            prepare.RaiseCanExecuteChanged();
        }

        if (SaveCommand is AsyncCommand save)
        {
            save.RaiseCanExecuteChanged();
        }

        if (CancelSaveCommand is DelegateCommand cancel)
        {
            cancel.RaiseCanExecuteChanged();
        }

    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(ErrorCode))
        {
            OnPropertyChanged(nameof(HasError));
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Storage,
        "problem.diagnostics.bundle_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.diagnostics.review_bundle"]));

    private sealed class DelegateCommand(Action<object?> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter) => await execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
