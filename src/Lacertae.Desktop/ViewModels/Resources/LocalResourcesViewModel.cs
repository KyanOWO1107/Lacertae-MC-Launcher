using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Platform;
using Lacertae.Application.Resources;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.ViewModels.Resources;

public sealed class LocalResourcesViewModel : INotifyPropertyChanged
{
    private readonly ResolveLocalResourceFolders resolver;
    private readonly IPlatformDialogService? dialogService;
    private readonly string sharedRoot;
    private readonly string? isolatedRoot;
    private string? errorCode;

    public LocalResourcesViewModel(
        string sharedRoot,
        string? isolatedRoot = null,
        IPlatformDialogService? dialogService = null,
        ResolveLocalResourceFolders? resolver = null)
    {
        this.sharedRoot = sharedRoot ?? throw new ArgumentNullException(nameof(sharedRoot));
        this.isolatedRoot = isolatedRoot;
        this.dialogService = dialogService;
        this.resolver = resolver ?? new ResolveLocalResourceFolders();
        OpenCommand = new DelegateCommand(OpenFolder);
        CreateCommand = new DelegateCommand(CreateFolder);
        ConfirmCreateCommand = new DelegateCommand(_ => ConfirmPendingCreate());
        CancelCreateCommand = new DelegateCommand(_ => CancelPendingCreate());
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<LocalResourceFolder> Folders { get; } = [];
    public string RootPath { get; private set; } = string.Empty;
    public string? ErrorCode { get => errorCode; private set { if (errorCode != value) { errorCode = value; PropertyChanged?.Invoke(this, new(nameof(ErrorCode))); PropertyChanged?.Invoke(this, new(nameof(HasError))); } } }
    public bool HasError => ErrorCode is not null;
    public ICommand OpenCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ConfirmCreateCommand { get; }
    public ICommand CancelCreateCommand { get; }
    public LocalResourceFolder? PendingFolder { get; private set; }
    public bool IsCreateConfirmationOpen => PendingFolder is not null;

    public void Reload()
    {
        Result<LocalResourceFolders> result = resolver.Resolve(sharedRoot, isolatedRoot);
        if (!result.IsSuccess) { ErrorCode = result.Problem?.Code; return; }
        ErrorCode = null;
        RootPath = result.Value.RootPath;
        Folders.Clear();
        foreach (LocalResourceFolder folder in result.Value.Folders) Folders.Add(folder);
        PropertyChanged?.Invoke(this, new(nameof(RootPath)));
    }

    public void OpenFolder(object? parameter)
    {
        if (parameter is not LocalResourceFolder folder || dialogService is null) return;
        try { dialogService.OpenDirectory(folder.NormalizedPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ErrorCode = "RESOURCE_OPEN_FAILED"; }
    }

    public void CreateFolder(object? parameter)
    {
        if (parameter is not LocalResourceFolder folder) return;
        if (ConfirmationRequested is not null && ConfirmationRequested(folder))
        {
            CompleteCreate(folder);
            return;
        }
        PendingFolder = folder;
        PropertyChanged?.Invoke(this, new(nameof(PendingFolder)));
        PropertyChanged?.Invoke(this, new(nameof(IsCreateConfirmationOpen)));
    }

    private void ConfirmPendingCreate()
    {
        if (PendingFolder is not null) CompleteCreate(PendingFolder);
    }

    private void CancelPendingCreate()
    {
        PendingFolder = null;
        PropertyChanged?.Invoke(this, new(nameof(PendingFolder)));
        PropertyChanged?.Invoke(this, new(nameof(IsCreateConfirmationOpen)));
    }

    private void CompleteCreate(LocalResourceFolder folder)
    {
        Result<LocalResourceFolder> result = resolver.Create(sharedRoot, isolatedRoot, folder.Name, confirmed: true);
        if (!result.IsSuccess) { ErrorCode = result.Problem?.Code; return; }
        PendingFolder = null;
        PropertyChanged?.Invoke(this, new(nameof(PendingFolder)));
        PropertyChanged?.Invoke(this, new(nameof(IsCreateConfirmationOpen)));
        Reload();
    }

    public Func<LocalResourceFolder, bool>? ConfirmationRequested { get; set; }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
}
