using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lacertae.Desktop.ViewModels.Startup;
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
            DesktopRecoveryHost recoveryHost = new(desktop, compositionRoot);
            _ = InitializeDesktopAsync(desktop, compositionRoot, recoveryHost, CancellationToken.None);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose() => compositionRoot?.Dispose();

    private static async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        CompositionRoot compositionRoot,
        IStartupRecoveryHost recoveryHost,
        CancellationToken cancellationToken)
    {
        try
        {
            Result<Lacertae.Application.Startup.StartupState> result =
                await compositionRoot.InitializeAsync(cancellationToken);
            desktop.MainWindow = result.IsSuccess
                ? compositionRoot.CreateMainWindow()
                : CompositionRoot.CreateRecoveryWindow(result.Problem!, recoveryHost);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            desktop.MainWindow = CompositionRoot.CreateRecoveryWindow(
                CompositionRoot.CreateStartupFailureProblem(),
                recoveryHost);
        }
    }

    private sealed class DesktopRecoveryHost(
        IClassicDesktopStyleApplicationLifetime desktop,
        CompositionRoot compositionRoot) : IStartupRecoveryHost
    {
        public bool CanRetry => true;

        public bool CanRestore => false;

        public bool CanOpenLog => false;

        public Task RetryAsync(CancellationToken cancellationToken) =>
            InitializeDesktopAsync(desktop, compositionRoot, this, cancellationToken);

        public Task RestoreAsync(CancellationToken cancellationToken) => Task.FromException(
            new NotSupportedException("Settings restore is not available from startup recovery."));

        public Task OpenLogAsync(CancellationToken cancellationToken) => Task.FromException(
            new NotSupportedException("Opening the startup log is not available from startup recovery."));
    }
}
