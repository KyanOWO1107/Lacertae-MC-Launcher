using Lacertae.Application.Java;
using Lacertae.Domain.Java;

namespace Lacertae.Desktop.ViewModels.Java;

public sealed record JavaRuntimeItem(
    string PrimaryLabel,
    string SecondaryLabel,
    string ExecutablePath,
    bool IsManaged)
{
    private readonly string pathLabel = "可执行文件路径";
    private readonly string copyPathLabel = "复制路径";

    public string PathLabel => pathLabel;
    public string CopyPathLabel => copyPathLabel;
}

public sealed class JavaSettingsViewModel
{
    private readonly string automaticOptionLabel = "自动选择";
    private readonly string addPathLabel = "添加路径";
    private readonly string installManagedLabel = "安装受管 Java";
    private readonly bool canAddPath = true;

    public JavaSettingsViewModel()
        : this(new JavaDiscoveryResult([], []), null, JavaArchitecture.Unknown)
    {
    }

    public JavaSettingsViewModel(
        JavaDiscoveryResult discovery,
        int? requiredMajor,
        JavaArchitecture preferredArchitecture)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(discovery.Installations);

        RequiredMajor = requiredMajor;
        PreferredArchitecture = preferredArchitecture;
        Runtimes = discovery.Installations
            .OrderByDescending(static installation => installation.IsManaged)
            .ThenByDescending(static installation => installation.MajorVersion)
            .ThenBy(static installation => installation.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static installation => installation.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(CreateItem)
            .ToArray();

        ShowInstallManagedAction = requiredMajor is not null &&
            !discovery.Installations.Any(installation => installation.MajorVersion == requiredMajor.Value);
        MissingRuntimeMessage = ShowInstallManagedAction
            ? $"未找到 Java {requiredMajor}，可安装受管运行时。"
            : null;
    }

    public IReadOnlyList<JavaRuntimeItem> Runtimes { get; }

    public JavaRuntimeItem? SelectedRuntime { get; set; }

    public int? RequiredMajor { get; }

    public JavaArchitecture PreferredArchitecture { get; }

    public string AutomaticOptionLabel => automaticOptionLabel;

    public string AddPathLabel => addPathLabel;

    public string InstallManagedLabel => installManagedLabel;

    public bool IsAutomaticSelected => SelectedRuntime is null;

    public bool CanAddPath => canAddPath;

    public bool ShowInstallManagedAction { get; }

    public string? MissingRuntimeMessage { get; }

    private static JavaRuntimeItem CreateItem(JavaInstallation installation) => new(
        $"Java {installation.MajorVersion} · {DisplayVendor(installation.Vendor)} · {DisplayArchitecture(installation.Architecture)}",
        $"{DisplaySource(installation.Source)} · {installation.FullVersion}{(installation.IsManaged ? " · 受管" : string.Empty)}",
        installation.ExecutablePath,
        installation.IsManaged);

    private static string DisplayVendor(string vendor) =>
        string.IsNullOrWhiteSpace(vendor) ? "未知供应商" : vendor;

    private static string DisplayArchitecture(JavaArchitecture architecture) => architecture switch
    {
        JavaArchitecture.X86 => "x86",
        JavaArchitecture.X64 => "x64",
        JavaArchitecture.Arm64 => "ARM64",
        _ => "未知架构",
    };

    private static string DisplaySource(JavaSource source) => source switch
    {
        JavaSource.Managed => "受管运行时",
        JavaSource.Path => "PATH",
        JavaSource.Registry => "Windows 注册表",
        JavaSource.CommonDirectory => "常见安装目录",
        JavaSource.Manual => "手动添加",
        _ => "未知来源",
    };
}
