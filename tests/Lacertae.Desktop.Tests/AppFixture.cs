using Avalonia;
using Avalonia.Headless;

namespace Lacertae.Desktop.Tests;

public static class AppFixture
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<Lacertae.Desktop.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        });
}
