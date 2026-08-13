using Avalonia.Controls;
using Lacertae.Desktop.Views;

namespace Lacertae.Desktop.Tests.Views;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void ShellExposesStableNavigationAndDisabledLaunchAction()
    {
        MainWindow window = new();
        window.Show();

        Assert.Equal("Lacertae", window.Title);
        Assert.NotNull(window.FindControl<Button>("LaunchButton"));
        Assert.False(window.FindControl<Button>("LaunchButton")!.IsEnabled);
        Assert.Equal("未选择游戏版本", window.FindControl<TextBlock>("VersionSummary")!.Text);
    }
}
