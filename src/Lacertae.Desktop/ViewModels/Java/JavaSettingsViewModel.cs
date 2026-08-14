using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Home;
using Lacertae.Application.Java;
using Lacertae.Domain.Common;
using Lacertae.Domain.Java;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.ViewModels.Java;

public sealed record JavaRuntimeItem(
    string PrimaryLabel,
    string SecondaryLabel,
    string ExecutablePath,
    bool IsManaged,
    int MajorVersion,
    JavaArchitecture Architecture)
{
    private readonly string pathLabel = "可执行文件路径";
    private readonly string copyPathLabel = "复制路径";

    public string PathLabel => pathLabel;
    public string CopyPathLabel => copyPathLabel;
}

public sealed class JavaSettingsViewModel : INotifyPropertyChanged
{
    private readonly IJavaProbe? javaProbe;
    private readonly string automaticOptionLabel = "自动选择";
    private readonly string addPathLabel = "添加路径";
    private readonly string installManagedLabel = "安装受管 Java";
    private readonly bool canAddPath;
    private readonly int? requiredMajor;
    private readonly JavaArchitecture preferredArchitecture;
    private readonly Func<string?, CancellationToken, Task<Result<Unit>>>? saveGlobalJavaPath;
    private readonly Func<CancellationToken, Task<Result<JavaInstallation>>>? installManagedJava;
    private JavaRuntimeItem? selectedRuntime;
    private string manualPathText = string.Empty;
    private string? selectionValidationMessage;
    private string? selectionValidationCode;
    private bool isBusy;

    public JavaSettingsViewModel()
        : this(new JavaDiscoveryResult([], []), null, JavaArchitecture.Unknown)
    {
    }

    public JavaSettingsViewModel(
        JavaDiscoveryResult discovery,
        int? requiredMajor,
        JavaArchitecture preferredArchitecture,
        IJavaProbe? javaProbe = null,
        Func<string?, CancellationToken, Task<Result<Unit>>>? saveGlobalJavaPath = null,
        Func<CancellationToken, Task<Result<JavaInstallation>>>? installManagedJava = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(discovery.Installations);
        if (!Enum.IsDefined(preferredArchitecture))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredArchitecture));
        }

        this.requiredMajor = requiredMajor;
        this.preferredArchitecture = preferredArchitecture;
        this.javaProbe = javaProbe;
        this.saveGlobalJavaPath = saveGlobalJavaPath;
        this.installManagedJava = installManagedJava;
        // The path editor is always available; probing is only enabled when
        // the host supplied a probe implementation.
        canAddPath = true;
        RequiredMajor = requiredMajor;
        PreferredArchitecture = preferredArchitecture;
        ApplyDiscovery(discovery);
        SelectAutomaticCommand = new AsyncCommand(
            () => SelectAutomaticAsync(CancellationToken.None),
            () => !IsBusy);
        useManualPathCommand = new AsyncCommand(
            () => UseManualPathAsync(CancellationToken.None),
            () => CanUseManualPath);
        InstallManagedCommand = new AsyncCommand(
            () => InstallManagedAsync(CancellationToken.None),
            () => ShowInstallManagedAction && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<JavaRuntimeItem> Runtimes { get; private set; } = [];

    public JavaRuntimeItem? SelectedRuntime
    {
        get => selectedRuntime;
        set
        {
            if (ReferenceEquals(selectedRuntime, value))
            {
                return;
            }

            selectedRuntime = value;
            ManualPathText = value?.ExecutablePath ?? string.Empty;
            ClearSelectionValidation();
            if (value is not null && !IsCompatible(value))
            {
                SetSelectionValidation("JAVA_MANUAL_INCOMPATIBLE", BuildCompatibilityMessage(value), retryable: false);
            }
            else if (value is not null)
            {
                _ = PersistGlobalJavaPathAsync(value.ExecutablePath, CancellationToken.None);
            }
            OnPropertyChanged(nameof(SelectedRuntime));
            OnPropertyChanged(nameof(IsAutomaticSelected));
            OnPropertyChanged(nameof(IsManualSelection));
            OnPropertyChanged(nameof(IsSelectedRuntimeIncompatible));
            OnPropertyChanged(nameof(SelectionValidationMessage));
            OnPropertyChanged(nameof(HasSelectionValidation));
        }
    }

    public string ManualPathText
    {
        get => manualPathText;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(manualPathText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            manualPathText = normalized;
            OnPropertyChanged(nameof(ManualPathText));
            OnPropertyChanged(nameof(CanUseManualPath));
            useManualPathCommand?.RaiseCanExecuteChanged();
        }
    }

    public int? RequiredMajor { get; }

    public JavaArchitecture PreferredArchitecture { get; }

    public string AutomaticOptionLabel => automaticOptionLabel;

    public string AddPathLabel => addPathLabel;

    public string InstallManagedLabel => installManagedLabel;

    public bool IsAutomaticSelected => SelectedRuntime is null;

    public bool IsManualSelection => SelectedRuntime is not null;

    public bool IsSelectedRuntimeIncompatible => SelectedRuntime is not null && !IsCompatible(SelectedRuntime);

    public bool CanAddPath => canAddPath;

    public bool CanUseManualPath => !IsBusy && CanAddPath && !string.IsNullOrWhiteSpace(ManualPathText);

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (isBusy == value)
            {
                return;
            }

            isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanUseManualPath));
            useManualPathCommand?.RaiseCanExecuteChanged();
            (InstallManagedCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string? SelectionValidationMessage => selectionValidationMessage;

    public string? SelectionValidationCode => selectionValidationCode;

    public bool HasSelectionValidation => !string.IsNullOrWhiteSpace(SelectionValidationMessage);

    public bool ShowInstallManagedAction { get; private set; }

    public string? MissingRuntimeMessage { get; private set; }

    public ICommand SelectAutomaticCommand { get; }

    public ICommand UseManualPathCommand => useManualPathCommand;

    public ICommand InstallManagedCommand { get; }

    private readonly AsyncCommand useManualPathCommand;

    public async Task UseManualPathAsync(CancellationToken cancellationToken)
    {
        if (javaProbe is null || string.IsNullOrWhiteSpace(ManualPathText))
        {
            SetSelectionValidation("JAVA_MANUAL_UNAVAILABLE", "手动 Java 检查暂不可用，请先输入路径。", retryable: false);
            return;
        }

        IsBusy = true;
        ClearSelectionValidation();
        try
        {
            Result<JavaInstallation> result = await javaProbe.ProbeAsync(
                ManualPathText.Trim(),
                JavaSource.Manual,
                false,
                cancellationToken);
            if (!result.IsSuccess)
            {
                SetSelectionValidation(
                    result.Problem?.Code ?? "JAVA_MANUAL_INVALID",
                    "手动 Java 路径无法使用；修复后仍会保留当前选择。",
                    retryable: true);
                return;
            }

            JavaRuntimeItem item = CreateItem(result.Value);
            selectedRuntime = item;
            manualPathText = item.ExecutablePath;
            OnPropertyChanged(nameof(SelectedRuntime));
            OnPropertyChanged(nameof(ManualPathText));
            OnPropertyChanged(nameof(IsAutomaticSelected));
            OnPropertyChanged(nameof(IsManualSelection));
            OnPropertyChanged(nameof(IsSelectedRuntimeIncompatible));
            if (IsSelectedRuntimeIncompatible)
            {
                SetSelectionValidation(
                    "JAVA_MANUAL_INCOMPATIBLE",
                    BuildCompatibilityMessage(item),
                    retryable: false);
                return;
            }

            await PersistGlobalJavaPathAsync(item.ExecutablePath, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAsync(IJavaDiscovery discovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        IsBusy = true;
        try
        {
            Result<JavaDiscoveryResult> result = await discovery.ExecuteAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                SetSelectionValidation(
                    result.Problem?.Code ?? "JAVA_DISCOVERY_FAILED",
                    "Java 自动发现失败；你仍可输入路径手动检查。",
                    retryable: true);
                return;
            }

            ApplyDiscovery(result.Value);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectAutomaticAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        selectedRuntime = null;
        manualPathText = string.Empty;
        ClearSelectionValidation();
        OnPropertyChanged(nameof(SelectedRuntime));
        OnPropertyChanged(nameof(ManualPathText));
        OnPropertyChanged(nameof(IsAutomaticSelected));
        OnPropertyChanged(nameof(IsManualSelection));
        OnPropertyChanged(nameof(IsSelectedRuntimeIncompatible));
        try
        {
            await PersistGlobalJavaPathAsync(null, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InstallManagedAsync(CancellationToken cancellationToken)
    {
        if (installManagedJava is null)
        {
            NotifyManagedInstallUnavailable();
            return;
        }

        if (requiredMajor is null)
        {
            SetSelectionValidation(
                "JAVA_MANAGED_INSTALL_UNAVAILABLE",
                "当前版本没有可用的 Java 要求，无法安装受管运行时。",
                retryable: false);
            return;
        }

        IsBusy = true;
        ClearSelectionValidation();
        try
        {
            Result<JavaInstallation> result = await installManagedJava(cancellationToken);
            if (!result.IsSuccess)
            {
                SetSelectionValidation(
                    result.Problem?.Code ?? "JAVA_MANAGED_INSTALL_FAILED",
                    "受管 Java 安装失败；请检查网络和下载源后重试。",
                    retryable: result.Problem?.IsRetryable == true);
                return;
            }

            JavaRuntimeItem item = CreateItem(result.Value);
            if (!IsCompatible(item))
            {
                SetSelectionValidation(
                    "JAVA_MANAGED_INSTALL_MISMATCH",
                    BuildCompatibilityMessage(item),
                    retryable: false);
                return;
            }

            Runtimes = Runtimes
                .Where(existing => !string.Equals(existing.ExecutablePath, item.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                .Append(item)
                .OrderByDescending(static runtime => runtime.IsManaged)
                .ThenByDescending(static runtime => runtime.MajorVersion)
                .ThenBy(static runtime => runtime.PrimaryLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ShowInstallManagedAction = false;
            MissingRuntimeMessage = null;
            selectedRuntime = item;
            manualPathText = item.ExecutablePath;
            OnPropertyChanged(nameof(Runtimes));
            OnPropertyChanged(nameof(ShowInstallManagedAction));
            OnPropertyChanged(nameof(MissingRuntimeMessage));
            OnPropertyChanged(nameof(SelectedRuntime));
            OnPropertyChanged(nameof(ManualPathText));
            OnPropertyChanged(nameof(IsAutomaticSelected));
            OnPropertyChanged(nameof(IsManualSelection));
            OnPropertyChanged(nameof(IsSelectedRuntimeIncompatible));
            await PersistGlobalJavaPathAsync(item.ExecutablePath, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyManagedInstallUnavailable() => SetSelectionValidation(
        "JAVA_MANAGED_INSTALL_UNAVAILABLE",
        "受管 Java 安装将在下载源和许可确认后提供；当前可手动添加本机 Java。",
        retryable: false);

    private async Task PersistGlobalJavaPathAsync(string? executablePath, CancellationToken cancellationToken)
    {
        if (saveGlobalJavaPath is null)
        {
            return;
        }

        Result<Unit> result;
        try
        {
            result = await saveGlobalJavaPath(executablePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetSelectionValidation(
                "JAVA_SETTINGS_SAVE_FAILED",
                "Java 选择已更新，但全局设置保存失败；请检查设置目录后重试。",
                retryable: true);
            return;
        }

        if (!result.IsSuccess)
        {
            SetSelectionValidation(
                result.Problem?.Code ?? "JAVA_SETTINGS_SAVE_FAILED",
                "Java 选择已更新，但全局设置保存失败；请检查设置目录后重试。",
                retryable: result.Problem?.IsRetryable == true);
        }
    }

    private bool IsCompatible(JavaRuntimeItem runtime) =>
        (!requiredMajor.HasValue || runtime.MajorVersion == requiredMajor.Value) &&
        (preferredArchitecture == JavaArchitecture.Unknown || runtime.Architecture == preferredArchitecture);

    private void ApplyDiscovery(JavaDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(discovery.Installations);
        Runtimes = discovery.Installations
            .OrderByDescending(static installation => installation.IsManaged)
            .ThenByDescending(static installation => installation.MajorVersion)
            .ThenBy(static installation => installation.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static installation => installation.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(CreateItem)
            .ToArray();
        ShowInstallManagedAction = requiredMajor is not null &&
            !discovery.Installations.Any(installation => IsCompatible(CreateItem(installation)));
        MissingRuntimeMessage = ShowInstallManagedAction
            ? $"未找到 Java {requiredMajor}，可安装受管运行时。"
            : null;
        OnPropertyChanged(nameof(Runtimes));
        OnPropertyChanged(nameof(ShowInstallManagedAction));
        OnPropertyChanged(nameof(MissingRuntimeMessage));
        (InstallManagedCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    private string BuildCompatibilityMessage(JavaRuntimeItem runtime)
    {
        string required = requiredMajor is int major
            ? $"Java {major}"
            : "所需 Java";
        string architecture = preferredArchitecture == JavaArchitecture.Unknown
            ? string.Empty
            : $"/{preferredArchitecture}";
        return $"当前选择 Java {runtime.MajorVersion}/{runtime.Architecture}，与 {required}{architecture} 不兼容；修复后仍会保留该选择。";
    }

    private void SetSelectionValidation(string code, string message, bool retryable)
    {
        selectionValidationCode = code;
        selectionValidationMessage = message;
        OnPropertyChanged(nameof(SelectionValidationCode));
        OnPropertyChanged(nameof(SelectionValidationMessage));
        OnPropertyChanged(nameof(HasSelectionValidation));
    }

    private void ClearSelectionValidation()
    {
        if (selectionValidationCode is null && selectionValidationMessage is null)
        {
            return;
        }

        selectionValidationCode = null;
        selectionValidationMessage = null;
        OnPropertyChanged(nameof(SelectionValidationCode));
        OnPropertyChanged(nameof(SelectionValidationMessage));
        OnPropertyChanged(nameof(HasSelectionValidation));
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

    private static JavaRuntimeItem CreateItem(JavaInstallation installation) => new(
        $"Java {installation.MajorVersion} · {DisplayVendor(installation.Vendor)} · {DisplayArchitecture(installation.Architecture)}",
        $"{DisplaySource(installation.Source)} · {installation.FullVersion}{(installation.IsManaged ? " · 受管" : string.Empty)}",
        installation.ExecutablePath,
        installation.IsManaged,
        installation.MajorVersion,
        installation.Architecture);

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

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter) => await execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
