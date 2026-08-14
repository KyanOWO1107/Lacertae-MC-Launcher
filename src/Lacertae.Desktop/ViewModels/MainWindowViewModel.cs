using Lacertae.Desktop.ViewModels.Java;

namespace Lacertae.Desktop.ViewModels;

public sealed class MainWindowViewModel
{
    private readonly string greeting = "欢迎使用 Lacertae";
    private readonly string versionSummary = "未选择游戏版本";
    private readonly bool canLaunch;

    public MainWindowViewModel() => canLaunch = false;

    public string Greeting => greeting;

    public string VersionSummary => versionSummary;

    public bool CanLaunch => canLaunch;

    public JavaSettingsViewModel JavaSettings { get; } = new();
}
