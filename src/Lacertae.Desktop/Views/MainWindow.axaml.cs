using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lacertae.Desktop.ViewModels;

namespace Lacertae.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelPropertyChanged;
        viewModel.SetViewportWidth(Width);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.SetViewportWidth(e.NewSize.Width);
        Thickness contentMargin = viewModel.IsCompactNavigation
            ? new Thickness(0, 94, 0, 0)
            : new Thickness(220, 0, 0, 0);
        ContentPanel.Margin = contentMargin;
        AccountsContentPanel.Margin = contentMargin;
        HomeContentPanel.Margin = contentMargin;
        VersionsContentPanel.Margin = contentMargin;
        DownloadsContentPanel.Margin = contentMargin;
    }

    private void NavigateFromButton(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            sender is not Button button ||
            button.Tag is not string routeId ||
            !viewModel.TryNavigate(routeId))
        {
            return;
        }

        FocusPageHeading();
        e.Handled = true;
    }

    private void FocusPageHeading()
    {
        PageHeading.Focus();
        if (DataContext is MainWindowViewModel viewModel && viewModel.IsGenericPageVisible)
        {
            Dispatcher.UIThread.Post(
                () => PageHeading.Focus(),
                DispatcherPriority.Loaded);
        }
    }

    private void ViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
        {
            FocusPageHeading();
        }
    }
}
