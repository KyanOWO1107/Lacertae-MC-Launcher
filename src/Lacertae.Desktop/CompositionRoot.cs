using Avalonia;
using Avalonia.Controls;
using Lacertae.Application.Startup;
using Lacertae.Application.Storage;
using Lacertae.Desktop.ViewModels;
using Lacertae.Desktop.Views;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;
using Lacertae.Infrastructure.Startup;
using Lacertae.Infrastructure.Storage;
using Lacertae.Platform.Windows.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Lacertae.Desktop;

public sealed class CompositionRoot : IDisposable
{
    private readonly ServiceProvider services;
    private StartupState? startupState;

    public CompositionRoot()
    {
        ServiceCollection registrations = new();
        registrations.AddSingleton<IPlatformPaths, WindowsPlatformPaths>();
        registrations.AddSingleton<IFileSystem, SystemFileSystem>();
        registrations.AddSingleton<DataRootResolver>();
        registrations.AddSingleton<IStartupDataRootResolver>(provider => provider.GetRequiredService<DataRootResolver>());
        registrations.AddSingleton<IStartupLoggingInitializer, FileLoggingInitializer>();
        registrations.AddSingleton<IStartupStorageFactory, DurableStartupStorageFactory>();
        registrations.AddSingleton<StartupCoordinator>();
        registrations.AddSingleton<MainWindowViewModel>();
        registrations.AddTransient<MainWindow>();
        services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task<Result<StartupState>> InitializeAsync(CancellationToken cancellationToken)
    {
        Result<StartupState> result = await services.GetRequiredService<StartupCoordinator>().InitializeAsync(cancellationToken);
        if (result.IsSuccess)
        {
            startupState = result.Value;
        }

        return result;
    }

    public MainWindow CreateMainWindow()
    {
        if (startupState is null)
        {
            throw new InvalidOperationException("Startup must succeed before creating the main window.");
        }

        return services.GetRequiredService<MainWindow>();
    }

    public static Window CreateRecoveryWindow(Problem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new Window
        {
            Title = "Lacertae - 启动恢复",
            Width = 520,
            Height = 260,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "启动器无法完成初始化", FontSize = 22 },
                    new TextBlock { Text = $"问题代码：{problem.Code}" },
                    new TextBlock { Text = "请检查日志后重试；敏感路径不会显示在此窗口。" },
                },
            },
        };
    }

    public void Dispose()
    {
        services.Dispose();
    }
}
