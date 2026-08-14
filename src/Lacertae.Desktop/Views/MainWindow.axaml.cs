using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        viewModel.SetViewportWidth(Width);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.SetViewportWidth(e.NewSize.Width);
        ContentPanel.Margin = viewModel.IsCompactNavigation
            ? new Thickness(0, 94, 0, 0)
            : new Thickness(220, 0, 0, 0);
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

        PageHeading.Focus();
        e.Handled = true;
    }
}
