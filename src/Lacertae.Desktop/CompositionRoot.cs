using Avalonia;
using Avalonia.Controls;
using Lacertae.Application.Accessibility;
using Lacertae.Application.Operations;
using Lacertae.Application.Startup;
using Lacertae.Application.Storage;
using Lacertae.Desktop.Services;
using Lacertae.Desktop.ViewModels;
using Lacertae.Desktop.Views;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;
using Lacertae.Infrastructure.Operations;
using Lacertae.Infrastructure.Startup;
using Lacertae.Infrastructure.Storage;
using Lacertae.Platform.Windows.Accessibility;
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
        registrations.AddSingleton<IMotionPreference, WindowsMotionPreference>();
        registrations.AddSingleton<ThemeService>(provider =>
            new ThemeService(provider.GetRequiredService<IMotionPreference>()));
        registrations.AddSingleton<IPlatformPaths, WindowsPlatformPaths>();
        registrations.AddSingleton<IFileSystem, SystemFileSystem>();
        registrations.AddSingleton<DataRootResolver>();
        registrations.AddSingleton<IStartupDataRootResolver>(provider => provider.GetRequiredService<DataRootResolver>());
        registrations.AddSingleton<IStartupLoggingInitializer, FileLoggingInitializer>();
        registrations.AddSingleton<IStartupStorageFactory, DurableStartupStorageFactory>();
        registrations.AddSingleton<IBackgroundTaskStore>(provider =>
        {
            Result<DataRoot> dataRoot = provider.GetRequiredService<DataRootResolver>().Resolve();
            if (!dataRoot.IsSuccess)
            {
                throw new InvalidOperationException($"Cannot resolve the background-task database: {dataRoot.Problem?.Code}");
            }

            return new SqliteBackgroundTaskStore(new SqliteConnectionFactory(dataRoot.Value.DatabasePath));
        });
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
            if (!IsValidSettings(result.Value.Settings))
            {
                return Result<StartupState>.Failure(CreateSettingsCorruptProblem());
            }

            startupState = result.Value;
            ApplyTheme(result.Value.Settings.Theme);
        }

        return result;
    }

    public void ApplyTheme(ThemeMode theme, bool reduceMotion = false) =>
        services.GetRequiredService<ThemeService>().Apply(theme, reduceMotion);

    public static Problem CreateStartupFailureProblem() => new(
        "STARTUP_FAILED",
        ProblemStage.Unknown,
        "problem.startup.failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.startup.retry"]);

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

    private static bool IsValidSettings(LauncherSettings settings) =>
        Enum.IsDefined(settings.Theme) && Enum.IsDefined(settings.IsolationPolicy);

    private static Problem CreateSettingsCorruptProblem() => new(
        "SETTINGS_CORRUPT",
        ProblemStage.Configuration,
        "problem.settings.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.settings.restore_backup"]);
}
