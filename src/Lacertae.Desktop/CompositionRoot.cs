using Lacertae.Desktop.ViewModels;
using Lacertae.Desktop.Views;

namespace Lacertae.Desktop;

public sealed class CompositionRoot : IDisposable
{
    private readonly MainWindowViewModel mainWindowViewModel = new();

    public MainWindow CreateMainWindow() => new(mainWindowViewModel);

    public void Dispose()
    {
    }
}
