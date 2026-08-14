using System.ComponentModel;
using System.Windows.Input;

namespace Lacertae.Desktop.ViewModels.Home;

public sealed class RepairPreviewViewModel : INotifyPropertyChanged
{
    private readonly DelegateCommand confirmDownloadCommand;
    private readonly bool canConfirmDownload;
    private bool isOpen;

    public RepairPreviewViewModel()
    {
        OpenCommand = new DelegateCommand(Open);
        CloseCommand = new DelegateCommand(Close);
        canConfirmDownload = false;
        confirmDownloadCommand = new DelegateCommand(ConfirmDownload, () => CanConfirmDownload);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsOpen => isOpen;

    public string Summary => isOpen
        ? "已检测到缺失或损坏的游戏文件。请检查修复预览后再确认。"
        : "尚未打开修复预览。";

    public string ConfirmationUnavailableReason => isOpen
        ? "修复下载将在后续版本提供。"
        : "打开预览后才可确认修复。";

    public bool CanConfirmDownload => isOpen && canConfirmDownload;

    public ICommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand ConfirmDownloadCommand => confirmDownloadCommand;

    public void Open()
    {
        if (isOpen)
        {
            return;
        }

        isOpen = true;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ConfirmationUnavailableReason));
    }

    public void Close()
    {
        if (!isOpen)
        {
            return;
        }

        isOpen = false;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ConfirmationUnavailableReason));
    }

    private void ConfirmDownload()
    {
        // Deliberately unreachable while repair execution is not available.
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

    private sealed class DelegateCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Func<bool> canExecute = canExecute ?? (() => true);

        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute();
    }
}
