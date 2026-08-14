using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Lacertae.Desktop.Tests.Design;

public sealed class DesignSystemTests
{
    [AvaloniaFact]
    public void SemanticPaletteProvidesLightAndDarkSurfaceAndTextTokens()
    {
        SolidColorBrush lightSurface = RequireBrush("SurfaceBaseBrush", ThemeVariant.Light);
        SolidColorBrush lightText = RequireBrush("TextPrimaryBrush", ThemeVariant.Light);
        SolidColorBrush lightSecondary = RequireBrush("TextSecondaryBrush", ThemeVariant.Light);
        SolidColorBrush lightMuted = RequireBrush("TextMutedBrush", ThemeVariant.Light);
        SolidColorBrush lightOnAccent = RequireBrush("TextOnAccentBrush", ThemeVariant.Light);
        SolidColorBrush darkSurface = RequireBrush("SurfaceBaseBrush", ThemeVariant.Dark);
        SolidColorBrush darkText = RequireBrush("TextPrimaryBrush", ThemeVariant.Dark);
        SolidColorBrush darkSecondary = RequireBrush("TextSecondaryBrush", ThemeVariant.Dark);
        SolidColorBrush darkMuted = RequireBrush("TextMutedBrush", ThemeVariant.Dark);
        SolidColorBrush darkOnAccent = RequireBrush("TextOnAccentBrush", ThemeVariant.Dark);

        Assert.True(Contrast(lightText.Color, lightSurface.Color) >= 4.5);
        Assert.True(Contrast(lightSecondary.Color, lightSurface.Color) >= 4.5);
        Assert.True(Contrast(lightMuted.Color, lightSurface.Color) >= 4.5);
        Assert.True(Contrast(darkText.Color, darkSurface.Color) >= 4.5);
        Assert.True(Contrast(darkSecondary.Color, darkSurface.Color) >= 4.5);
        Assert.True(Contrast(darkMuted.Color, darkSurface.Color) >= 4.5);

        foreach (string accentKey in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush" })
        {
            Assert.True(Contrast(lightOnAccent.Color, RequireBrush(accentKey, ThemeVariant.Light).Color) >= 3);
            Assert.True(Contrast(darkOnAccent.Color, RequireBrush(accentKey, ThemeVariant.Dark).Color) >= 3);
        }
    }

    [AvaloniaFact]
    public void DesignTokensExposeSpacingCornersElevationTypographyAndMotion()
    {
        foreach (string key in new[] { "Spacing4", "Spacing8", "Spacing12", "Spacing16", "Spacing24", "Spacing32" })
        {
            Assert.True(Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out object? value));
            Assert.NotNull(value);
        }

        foreach (string key in new[] { "CornerRadius8", "CornerRadius12", "CornerRadius16", "Elevation1", "Elevation2", "Elevation3", "DefaultFontFamily" })
        {
            Assert.True(Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out object? value));
            Assert.NotNull(value);
        }

        Assert.True(Avalonia.Application.Current!.TryGetResource("MotionDuration", ThemeVariant.Light, out object? duration));
        Assert.Equal(TimeSpan.FromMilliseconds(150), Assert.IsType<TimeSpan>(duration));
        Assert.True(Avalonia.Application.Current.TryGetResource("MotionTransitions", ThemeVariant.Light, out object? transitions));
        Assert.Single(Assert.IsType<Transitions>(transitions));
    }

    [AvaloniaFact]
    public void FocusRingBrushIsAvailableForKeyboardFocusStyles()
    {
        SolidColorBrush focus = RequireBrush("FocusRingBrush", ThemeVariant.Light);
        Assert.NotEqual(Colors.Transparent, focus.Color);
    }

    [AvaloniaFact]
    public void TypographyUsesTheWindowsFallbackFontOrder()
    {
        Assert.True(Avalonia.Application.Current!.TryGetResource("DefaultFontFamily", ThemeVariant.Light, out object? value));
        FontFamily family = Assert.IsType<FontFamily>(value);

        string familyOrder = string.Join(", ", family.FamilyNames);
        Assert.StartsWith("Segoe UI Variable", familyOrder, StringComparison.Ordinal);
        Assert.Contains("Segoe UI", familyOrder, StringComparison.Ordinal);
        Assert.EndsWith("sans-serif", familyOrder, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void KeyboardFocusUsesTheVisibleFocusRingToken()
    {
        Button button = new() { Content = "Focus" };
        Window window = new() { Content = button };
        window.Show();

        button.Focus();

        Assert.Equal(2, button.BorderThickness.Left);
        Assert.Equal(RequireBrush("FocusRingBrush", ThemeVariant.Light), button.BorderBrush);

        window.Close();
    }

    [AvaloniaFact]
    public void DesktopPagesUseSemanticColorResources()
    {
        foreach (string relativePath in new[]
        {
            "src/Lacertae.Desktop/Views/MainWindow.axaml",
            "src/Lacertae.Desktop/Views/Java/JavaSettingsView.axaml",
        })
        {
            string markup = File.ReadAllText(FindWorkspaceFile(relativePath));
            Assert.DoesNotContain("#", markup, StringComparison.Ordinal);
            Assert.Contains("DynamicResource", markup, StringComparison.Ordinal);
        }
    }

    private static SolidColorBrush RequireBrush(string key, ThemeVariant variant)
    {
        Assert.True(Avalonia.Application.Current!.TryGetResource(key, variant, out object? value));
        return Assert.IsType<SolidColorBrush>(value);
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(byte value)
        {
            double normalized = value / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        double foregroundLuminance = 0.2126 * Channel(foreground.R)
            + 0.7152 * Channel(foreground.G)
            + 0.0722 * Channel(foreground.B);
        double backgroundLuminance = 0.2126 * Channel(background.R)
            + 0.7152 * Channel(background.G)
            + 0.0722 * Channel(background.B);
        double lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        double darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static string FindWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Cannot locate workspace file '{relativePath}'.");
    }
}
