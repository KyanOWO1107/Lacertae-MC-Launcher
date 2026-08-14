using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

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
            compositionRoot.ApplyTheme(ThemeMode.System);
            desktop.Exit += (_, _) => compositionRoot.Dispose();
            _ = InitializeDesktopAsync(desktop, compositionRoot);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose() => compositionRoot?.Dispose();

    private static async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        CompositionRoot compositionRoot)
    {
        try
        {
            Result<Lacertae.Application.Startup.StartupState> result =
                await compositionRoot.InitializeAsync(CancellationToken.None);
            desktop.MainWindow = result.IsSuccess
                ? compositionRoot.CreateMainWindow()
                : CompositionRoot.CreateRecoveryWindow(result.Problem!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            desktop.MainWindow = CompositionRoot.CreateRecoveryWindow(CompositionRoot.CreateStartupFailureProblem());
        }
    }
}
