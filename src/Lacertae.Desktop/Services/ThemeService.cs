using Avalonia.Animation;
using Avalonia.Styling;
using Lacertae.Application.Accessibility;
using Lacertae.Domain.Settings;

namespace Lacertae.Desktop.Services;

public sealed class ThemeService
{
    private static readonly TimeSpan StandardMotionDuration = TimeSpan.FromMilliseconds(150);
    private readonly Avalonia.Application application;
    private readonly IMotionPreference motionPreference;

    public ThemeService(Avalonia.Application application, IMotionPreference motionPreference)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.motionPreference = motionPreference ?? throw new ArgumentNullException(nameof(motionPreference));
    }

    public ThemeService(IMotionPreference motionPreference)
        : this(Avalonia.Application.Current ?? throw new InvalidOperationException("An Avalonia application is required."), motionPreference)
    {
    }

    public ThemeMode CurrentTheme { get; private set; } = ThemeMode.System;

    public bool ReduceMotion { get; private set; }

    public void Apply(ThemeMode theme, bool reduceMotion = false)
    {
        ThemeVariant requestedTheme = theme switch
        {
            ThemeMode.System => ThemeVariant.Default,
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme mode."),
        };

        application.RequestedThemeVariant = requestedTheme;
        CurrentTheme = theme;
        ReduceMotion = reduceMotion || motionPreference.ReduceMotion;
        application.Resources["ReduceMotion"] = ReduceMotion;
        application.Resources["MotionDuration"] = ReduceMotion ? TimeSpan.Zero : StandardMotionDuration;
        application.Resources["MotionTransformDistance"] = ReduceMotion ? 0d : 4d;
        application.Resources["MotionTransitions"] = ReduceMotion ? new Transitions() : CreateStandardTransitions();
    }

    private static Transitions CreateStandardTransitions() => new()
    {
        new DoubleTransition
        {
            Property = Avalonia.Controls.Control.OpacityProperty,
            Duration = StandardMotionDuration,
        },
    };
}
