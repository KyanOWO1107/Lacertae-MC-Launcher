using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;
using Lacertae.Application.Accessibility;
using Lacertae.Desktop;
using Lacertae.Desktop.Services;
using Lacertae.Domain.Settings;
using Lacertae.Platform.Windows.Accessibility;

namespace Lacertae.Desktop.Tests.Design;

public sealed class ThemeServiceTests
{
    [AvaloniaFact]
    public void ThemeServiceIsExposedByTheDesktopAssembly()
    {
        ThemeService service = new(Avalonia.Application.Current!, new StubMotionPreference(false));

        service.Apply(ThemeMode.Light);

        Assert.Equal(ThemeMode.Light, service.CurrentTheme);
        Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void SystemThemeUsesAvaloniaDefaultRequestedVariant()
    {
        ThemeService service = new(Avalonia.Application.Current!, new StubMotionPreference(false));
        service.Apply(ThemeMode.System);

        Assert.True(Avalonia.Application.Current!.TryGetResource("MotionDuration", ThemeVariant.Default, out object? duration));
        Assert.Equal(TimeSpan.FromMilliseconds(150), Assert.IsType<TimeSpan>(duration));
        Assert.Equal(ThemeVariant.Default, Avalonia.Application.Current.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void OperatingSystemMotionPreferenceOverridesStandardMotion()
    {
        ThemeService service = new(Avalonia.Application.Current!, new StubMotionPreference(true));
        service.Apply(ThemeMode.Dark, reduceMotion: false);

        Assert.True(service.ReduceMotion);
        Assert.True(Avalonia.Application.Current!.TryGetResource("MotionDuration", ThemeVariant.Dark, out object? duration));
        Assert.Equal(TimeSpan.Zero, Assert.IsType<TimeSpan>(duration));
        Assert.True(Avalonia.Application.Current.TryGetResource("MotionTransitions", ThemeVariant.Dark, out object? transitions));
        Assert.Empty(Assert.IsType<Transitions>(transitions));
    }

    [AvaloniaFact]
    public void InvalidThemeDoesNotPartiallyUpdateThemeState()
    {
        ThemeService service = new(Avalonia.Application.Current!, new StubMotionPreference(false));
        service.Apply(ThemeMode.Light);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Apply((ThemeMode)99));

        Assert.Equal(ThemeMode.Light, service.CurrentTheme);
        Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);
        Assert.False(service.ReduceMotion);
    }

    [AvaloniaFact]
    public void CompositionRootCanApplySystemThemeBeforeStartup()
    {
        using CompositionRoot compositionRoot = new();
        compositionRoot.ApplyTheme(ThemeMode.System);

        Assert.Equal(ThemeVariant.Default, Avalonia.Application.Current!.RequestedThemeVariant);
        Assert.True(Avalonia.Application.Current.TryGetResource("MotionDuration", ThemeVariant.Default, out object? duration));
        Assert.NotNull(duration);
        Assert.True(Avalonia.Application.Current.TryGetResource("ReduceMotion", ThemeVariant.Default, out object? reducedMotion));
        Assert.Equal(new WindowsMotionPreference().ReduceMotion, Assert.IsType<bool>(reducedMotion));
    }

    private sealed class StubMotionPreference(bool reduceMotion) : IMotionPreference
    {
        public bool ReduceMotion { get; } = reduceMotion;
    }
}
