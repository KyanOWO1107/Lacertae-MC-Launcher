using Avalonia.Controls;
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
        InitializeComponent();
        DataContext = viewModel;
    }
}
