using Avalonia;
using Avalonia.Headless;

namespace Lacertae.Desktop.Tests;

public static class AppFixture
{
    public static AppBuilder BuildAvaloniaApp()
    {
        AvaloniaHeadlessPlatformOptions options = new()
        {
            UseHeadlessDrawing = false,
        };

        // Avalonia 12.1.1 does not expose OverlayPopups yet; keep the harness
        // compatible with builds that do by applying the option when present.
        options.GetType().GetProperty("OverlayPopups")?.SetValue(options, false);

        return AppBuilder
            .Configure<Lacertae.Desktop.App>()
            .UseSkia()
            .UseHeadless(options);
    }
}
