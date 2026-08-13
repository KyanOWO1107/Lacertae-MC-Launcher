using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Lacertae.Desktop;

public sealed class App : Avalonia.Application, IDisposable
{
    private CompositionRoot? compositionRoot;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            compositionRoot = new CompositionRoot();
            desktop.MainWindow = compositionRoot.CreateMainWindow();
            desktop.Exit += (_, _) => compositionRoot.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose() => compositionRoot?.Dispose();
}
