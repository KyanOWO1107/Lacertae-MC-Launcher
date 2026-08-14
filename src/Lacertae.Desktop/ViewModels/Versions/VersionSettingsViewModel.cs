using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using Lacertae.Application.Java;
using Lacertae.Application.Operations;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Java;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Desktop.ViewModels.Versions;

public sealed record VersionRenamePreviewViewModel(
    VersionRenamePlan Plan,
    string SourceSummary,
    string TargetSummary,
    string IsolatedDataSummary,
    string RecoverySummary);

public sealed record GcOptionViewModel(
    GcProfile Profile,
    string Label,
    bool IsEnabled,
    string? DisabledReason)
{
    public bool IsDisabled => !IsEnabled;
}

public sealed class VersionSettingsViewModel : INotifyPropertyChanged
{
    private const int MaxArgumentBytes = 8 * 1024;
    private readonly SaveVersionOverride saveVersionOverride;
    private readonly RenameVersionFolder? renameVersionFolder;
    private readonly IJavaProbe? javaProbe;
    private readonly IBackgroundTaskStore? backgroundTaskStore;
    private readonly GameRoot root;
    private readonly ListedGameVersion version;
    private string displayNameDraft;
    private IsolationOverride isolationOverride;
    private string? accountIdDraft;
    private string? javaPathDraft;
    private string? minimumMemoryText;
    private string? maximumMemoryText;
    private GcProfile? gcProfileDraft;
    private string jvmArgumentsText;
    private string gameArgumentsText;
    private string renameTargetFolderDraft;
    private bool hasActiveBackgroundTask;
    private bool isBusy;
    private string? validationError;
    private string? validationErrorCode;
    private int? validationLineIndex;
    private Problem? lastProblem;
    private VersionRenamePreviewViewModel? renamePreview;
    private JavaArchitecture? javaArchitectureDraft;
    private string? renameReferenceSummary;

    public VersionSettingsViewModel(
        GameRoot root,
        ListedGameVersion version,
        SaveVersionOverride saveVersionOverride,
        RenameVersionFolder? renameVersionFolder = null,
        IJavaProbe? javaProbe = null,
        IBackgroundTaskStore? backgroundTaskStore = null,
        JavaArchitecture? initialJavaArchitecture = null)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.version = version ?? throw new ArgumentNullException(nameof(version));
        this.saveVersionOverride = saveVersionOverride ?? throw new ArgumentNullException(nameof(saveVersionOverride));
        this.renameVersionFolder = renameVersionFolder;
        this.javaProbe = javaProbe;
        this.backgroundTaskStore = backgroundTaskStore;
        if (initialJavaArchitecture is not null && !Enum.IsDefined(initialJavaArchitecture.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(initialJavaArchitecture));
        }
        displayNameDraft = version.DisplayName;
        isolationOverride = version.Settings.Isolation;
        accountIdDraft = version.AccountId;
        javaPathDraft = version.JavaPath;
        minimumMemoryText = version.MinimumMemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        maximumMemoryText = version.MaximumMemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        gcProfileDraft = version.GcProfile;
        javaArchitectureDraft = initialJavaArchitecture;
        jvmArgumentsText = string.Join(Environment.NewLine, version.JvmArguments);
        gameArgumentsText = string.Join(Environment.NewLine, version.GameArguments);
        renameTargetFolderDraft = version.FolderName;
        SaveCommand = new AsyncCommand(() => SaveAsync(CancellationToken.None), () => !IsBusy);
        PrepareRenameCommand = new AsyncCommand(() => PrepareRenameAsync(HasActiveBackgroundTask, CancellationToken.None), () => !IsBusy);
        ConfirmRenameCommand = new AsyncCommand(() => ConfirmRenameAsync(CancellationToken.None), () => CanConfirmRename);
        CancelRenameCommand = new DelegateCommand(_ => CloseRenamePreview());
    }

    public VersionSettingsViewModel(
        VersionRowViewModel row,
        SaveVersionOverride saveVersionOverride,
        RenameVersionFolder? renameVersionFolder = null,
        IJavaProbe? javaProbe = null,
        IBackgroundTaskStore? backgroundTaskStore = null,
        JavaArchitecture? initialJavaArchitecture = null)
        : this(row?.Root ?? throw new ArgumentNullException(nameof(row)), row.Version, saveVersionOverride, renameVersionFolder, javaProbe, backgroundTaskStore, initialJavaArchitecture)
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? Changed;

    public GameRoot Root => root;

    public ListedGameVersion Version => version;

    public string FolderName => version.FolderName;

    public string DisplayNameDraft
    {
        get => displayNameDraft;
        set => Set(ref displayNameDraft, value ?? string.Empty, nameof(DisplayNameDraft));
    }

    public IsolationOverride IsolationOverride
    {
        get => isolationOverride;
        set
        {
            if (!Enum.IsDefined(value))
            {
                SetValidation("VERSION_ISOLATION_INVALID", "请选择有效的隔离策略。", null);
                return;
            }

            Set(ref isolationOverride, value, nameof(IsolationOverride));
        }
    }

    public IReadOnlyList<IsolationOverride> IsolationOptions { get; } =
        Enum.GetValues<IsolationOverride>();

    public string? AccountIdDraft
    {
        get => accountIdDraft;
        set => Set(ref accountIdDraft, value, nameof(AccountIdDraft));
    }

    public string? JavaPathDraft
    {
        get => javaPathDraft;
        set => Set(ref javaPathDraft, value, nameof(JavaPathDraft));
    }

    public string? MinimumMemoryText
    {
        get => minimumMemoryText;
        set
        {
            Set(ref minimumMemoryText, value, nameof(MinimumMemoryText));
            OnPropertyChanged(nameof(MemoryModeLabel));
        }
    }

    public string? MaximumMemoryText
    {
        get => maximumMemoryText;
        set
        {
            Set(ref maximumMemoryText, value, nameof(MaximumMemoryText));
            OnPropertyChanged(nameof(MemoryModeLabel));
        }
    }

    public string MemoryModeLabel => string.IsNullOrWhiteSpace(MinimumMemoryText) &&
        string.IsNullOrWhiteSpace(MaximumMemoryText)
        ? "自动内存（留空，由启动器按物理内存和版本类型选择）"
        : "固定内存（最小和最大值需同时填写）";

    public GcProfile? GcProfileDraft
    {
        get => gcProfileDraft;
        set
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                SetValidation("VERSION_GC_INVALID", "请选择有效的 GC 策略。", null);
                return;
            }

            Set(ref gcProfileDraft, value, nameof(GcProfileDraft));
            OnPropertyChanged(nameof(SelectedGcOption));
            OnPropertyChanged(nameof(GcOptionItems));
        }
    }

    public IReadOnlyList<GcProfile> GcOptions { get; } = Enum.GetValues<GcProfile>();

    public GcOptionViewModel? SelectedGcOption
    {
        get => GcOptionItems.FirstOrDefault(option => option.Profile == (GcProfileDraft ?? GcProfile.Automatic));
        set
        {
            if (value is { IsEnabled: false })
            {
                SetValidation(
                    "JVM_GC_INCOMPATIBLE",
                    value.DisabledReason ?? "所选 GC 与当前 Java 不兼容。",
                    null);
                return;
            }

            GcProfileDraft = value?.Profile;
        }
    }

    public JavaArchitecture? JavaArchitectureDraft
    {
        get => javaArchitectureDraft;
        set
        {
            if (javaArchitectureDraft == value)
            {
                return;
            }

            javaArchitectureDraft = value;
            OnPropertyChanged(nameof(JavaArchitectureDraft));
            OnPropertyChanged(nameof(GcOptionItems));
        }
    }

    public IReadOnlyList<GcOptionViewModel> GcOptionItems =>
        GcOptions
            .Select(profile => CreateGcOption(profile, version.Java.MajorVersion, JavaArchitectureDraft))
            .ToArray();

    public string JvmArgumentsText
    {
        get => jvmArgumentsText;
        set => Set(ref jvmArgumentsText, value ?? string.Empty, nameof(JvmArgumentsText));
    }

    public string GameArgumentsText
    {
        get => gameArgumentsText;
        set => Set(ref gameArgumentsText, value ?? string.Empty, nameof(GameArgumentsText));
    }

    public string RenameTargetFolderDraft
    {
        get => renameTargetFolderDraft;
        set => Set(ref renameTargetFolderDraft, value ?? string.Empty, nameof(RenameTargetFolderDraft));
    }

    public bool HasActiveBackgroundTask
    {
        get => hasActiveBackgroundTask;
        set => Set(ref hasActiveBackgroundTask, value, nameof(HasActiveBackgroundTask));
    }

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
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanConfirmRename));
        }
    }

    public bool CanSave => !IsBusy && IsValid;

    public bool IsValid => string.IsNullOrWhiteSpace(ValidationError);

    public string? ValidationError
    {
        get => validationError;
        private set
        {
            if (string.Equals(validationError, value, StringComparison.Ordinal))
            {
                return;
            }

            validationError = value;
            OnPropertyChanged(nameof(ValidationError));
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public string? ValidationErrorCode
    {
        get => validationErrorCode;
        private set => Set(ref validationErrorCode, value, nameof(ValidationErrorCode));
    }

    public int? ValidationLineIndex
    {
        get => validationLineIndex;
        private set => Set(ref validationLineIndex, value, nameof(ValidationLineIndex));
    }

    public bool HasValidationLine => ValidationLineIndex is not null;

    public Problem? LastProblem
    {
        get => lastProblem;
        private set => Set(ref lastProblem, value, nameof(LastProblem));
    }

    public VersionRenamePreviewViewModel? RenamePreview
    {
        get => renamePreview;
        private set
        {
            if (ReferenceEquals(renamePreview, value))
            {
                return;
            }

            renamePreview = value;
            OnPropertyChanged(nameof(RenamePreview));
            OnPropertyChanged(nameof(IsRenamePreviewOpen));
            OnPropertyChanged(nameof(CanConfirmRename));
        }
    }

    public bool IsRenamePreviewOpen => RenamePreview is not null;

    public bool IsJavaSelectionInvalid => ValidationErrorCode is "JAVA_MANUAL_INVALID" or "JAVA_MANUAL_INCOMPATIBLE";

    public string? RenameReferenceSummary
    {
        get => renameReferenceSummary;
        private set
        {
            Set(ref renameReferenceSummary, value, nameof(RenameReferenceSummary));
            OnPropertyChanged(nameof(HasRenameReferenceSummary));
        }
    }

    public bool HasRenameReferenceSummary => !string.IsNullOrWhiteSpace(RenameReferenceSummary);

    public bool CanConfirmRename => !IsBusy && RenamePreview is not null && renameVersionFolder is not null;

    public ICommand SaveCommand { get; }

    public ICommand PrepareRenameCommand { get; }

    public ICommand ConfirmRenameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    public async Task<Result<Unit>> SaveAsync(CancellationToken cancellationToken)
    {
        ClearValidation();
        LastProblem = null;
        RenameReferenceSummary = null;
        Result<VersionOverride> draft = BuildDraft();
        if (!draft.IsSuccess)
        {
            LastProblem = draft.Problem;
            return Result.Failure(draft.Problem!);
        }

        IsBusy = true;
        try
        {
            if (javaProbe is not null && !string.IsNullOrWhiteSpace(draft.Value.JavaPath))
            {
                Result<JavaInstallation> probe = await javaProbe.ProbeAsync(
                    draft.Value.JavaPath,
                    JavaSource.Manual,
                    false,
                    cancellationToken);
                if (!probe.IsSuccess)
                {
                    SetValidation("JAVA_MANUAL_INVALID", "手动 Java 路径无法使用；修复后仍会保留该选择。", null);
                    LastProblem = probe.Problem;
                    return Result.Failure(probe.Problem!);
                }

                if (probe.Value.MajorVersion != version.Java.MajorVersion)
                {
                    Problem problem = Problem(
                        "JAVA_MANUAL_INCOMPATIBLE",
                        ProblemStage.JavaResolution,
                        "手动 Java 与当前版本不兼容；修复后仍会保留该选择。");
                    SetValidation(problem.Code, "手动 Java 与当前版本不兼容；修复后仍会保留该选择。", null);
                    LastProblem = problem;
                    return Result.Failure(problem);
                }

                JavaArchitectureDraft = probe.Value.Architecture;
            }

            Result<Unit> gcCompatibility = ValidateGcCompatibility(
                draft.Value.GcProfile ?? GcProfile.Automatic,
                version.Java.MajorVersion,
                JavaArchitectureDraft);
            if (!gcCompatibility.IsSuccess)
            {
                LastProblem = gcCompatibility.Problem;
                SetValidation(gcCompatibility.Problem!.Code, "当前 Java 不支持所选 GC，已保留该选择。", null);
                return gcCompatibility;
            }

            Result<Unit> result = await saveVersionOverride.ExecuteAsync(draft.Value, cancellationToken);
            if (!result.IsSuccess)
            {
                LastProblem = result.Problem;
                SetValidation(result.Problem!.Code, "版本设置未保存，请检查输入。", null);
                return result;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<Result<VersionRenamePlan>> PrepareRenameAsync(
        bool hasActiveBackgroundTask,
        CancellationToken cancellationToken)
    {
        ClearValidation();
        LastProblem = null;
        RenameReferenceSummary = null;
        if (renameVersionFolder is null)
        {
            Problem problem = Problem("VERSION_RENAME_UNAVAILABLE", ProblemStage.Storage, "当前版本不支持文件夹重命名。");
            LastProblem = problem;
            SetValidation(problem.Code, problem.MessageKey, null);
            return Result<VersionRenamePlan>.Failure(problem);
        }

        Result<bool> activeTask = await ResolveActiveBackgroundTaskAsync(
            hasActiveBackgroundTask,
            cancellationToken);
        if (!activeTask.IsSuccess)
        {
            return Result<VersionRenamePlan>.Failure(activeTask.Problem!);
        }

        HasActiveBackgroundTask = activeTask.Value;
        IsBusy = true;
        try
        {
            Result<VersionRenamePlan> result = await RenameVersionFolder.PrepareAsync(
                root.Id,
                root.NormalizedPath,
                version.FolderName,
                RenameTargetFolderDraft,
                activeTask.Value,
                cancellationToken);
            if (!result.IsSuccess)
            {
                LastProblem = result.Problem;
                string message = result.Problem!.SafeContext.TryGetValue("referringFolders", out string? references)
                    ? $"其他版本引用了此版本：{references}。请先处理引用后重试。"
                    : "无法生成重命名预览，请修复问题后重试。";
                RenameReferenceSummary = references;
                SetValidation(result.Problem.Code, message, null);
                RenamePreview = null;
                return result;
            }

            VersionRenamePlan plan = result.Value;
            RenamePreview = new VersionRenamePreviewViewModel(
                plan,
                $"来源：{plan.SourcePath}",
                $"目标：{plan.TargetPath}",
                plan.ContainsIsolatedGameData
                    ? "此版本包含隔离数据；数据会随版本目录一起移动。"
                    : "未检测到需要单独迁移的隔离数据。",
                "操作前会写入恢复日志；中断后可在下次启动继续完成或回滚。");
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<Result<VersionRenamePlan>> ConfirmRenameAsync(CancellationToken cancellationToken)
    {
        if (RenamePreview is null)
        {
            Problem problem = Problem("VERSION_RENAME_PREVIEW_REQUIRED", ProblemStage.Storage, "请先查看重命名预览。");
            LastProblem = problem;
            SetValidation(problem.Code, problem.MessageKey, null);
            return Result<VersionRenamePlan>.Failure(problem);
        }

        if (renameVersionFolder is null)
        {
            Problem problem = Problem("VERSION_RENAME_UNAVAILABLE", ProblemStage.Storage, "当前版本不支持文件夹重命名。");
            LastProblem = problem;
            SetValidation(problem.Code, problem.MessageKey, null);
            return Result<VersionRenamePlan>.Failure(problem);
        }

        Result<bool> activeTask = await ResolveActiveBackgroundTaskAsync(
            HasActiveBackgroundTask,
            cancellationToken);
        if (!activeTask.IsSuccess)
        {
            return Result<VersionRenamePlan>.Failure(activeTask.Problem!);
        }

        HasActiveBackgroundTask = activeTask.Value;
        IsBusy = true;
        try
        {
            Result<VersionRenamePlan> result = await renameVersionFolder.ExecuteAsync(
                root.Id,
                root.NormalizedPath,
                RenamePreview.Plan.SourceFolder,
                RenamePreview.Plan.TargetFolder,
                HasActiveBackgroundTask,
                cancellationToken);
            if (!result.IsSuccess)
            {
                LastProblem = result.Problem;
                SetValidation(result.Problem!.Code, "重命名未完成；请查看恢复说明后重试。", null);
                return result;
            }

            RenamePreview = null;
            RenameTargetFolderDraft = result.Value.TargetFolder;
            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void CloseRenamePreview() => RenamePreview = null;

    private async Task<Result<bool>> ResolveActiveBackgroundTaskAsync(
        bool callerReportedActiveTask,
        CancellationToken cancellationToken)
    {
        if (backgroundTaskStore is null)
        {
            return Result<bool>.Success(callerReportedActiveTask);
        }

        Result<IReadOnlyList<OperationSnapshot>> result = await backgroundTaskStore.GetActiveAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            Problem problem = Problem(
                "VERSION_RENAME_TASK_STATE_UNAVAILABLE",
                ProblemStage.Storage,
                "无法确认后台任务状态，已阻止重命名；请稍后重试。");
            LastProblem = problem;
            SetValidation(problem.Code, "无法确认后台任务状态，已阻止重命名；请稍后重试。", null);
            return Result<bool>.Failure(problem);
        }

        // The store currently exposes active operations without a version
        // scope. Treat any active operation as a reason to keep the physical
        // versions tree stable until the task finishes.
        return Result<bool>.Success(callerReportedActiveTask || result.Value.Count > 0);
    }

    private Result<VersionOverride> BuildDraft()
    {
        if (!Enum.IsDefined(IsolationOverride))
        {
            return InvalidDraft("VERSION_ISOLATION_INVALID", "请选择有效的隔离策略。", null);
        }

        if (string.IsNullOrWhiteSpace(DisplayNameDraft))
        {
            return InvalidDraft("VERSION_DISPLAY_NAME_INVALID", "显示名称不能为空。", null);
        }

        Result<int?> minimum = ParseMemory(MinimumMemoryText, nameof(MinimumMemoryText));
        if (!minimum.IsSuccess)
        {
            return Result<VersionOverride>.Failure(minimum.Problem!);
        }

        Result<int?> maximum = ParseMemory(MaximumMemoryText, nameof(MaximumMemoryText));
        if (!maximum.IsSuccess)
        {
            return Result<VersionOverride>.Failure(maximum.Problem!);
        }

        if (minimum.Value is int minimumValue && maximum.Value is int maximumValue && maximumValue < minimumValue)
        {
            return InvalidDraft("VERSION_MEMORY_CONFLICT", "最大内存不能小于最小内存。", 1);
        }

        if (minimum.Value.HasValue != maximum.Value.HasValue)
        {
            return InvalidDraft("VERSION_MEMORY_PAIR_REQUIRED", "固定内存必须同时填写最小和最大 MB；都留空才表示自动。", 1);
        }

        Result<IReadOnlyList<string>> jvm = ParseArguments(JvmArgumentsText, "JVM");
        if (!jvm.IsSuccess)
        {
            return Result<VersionOverride>.Failure(jvm.Problem!);
        }

        Result<IReadOnlyList<string>> game = ParseArguments(GameArgumentsText, "游戏");
        if (!game.IsSuccess)
        {
            return Result<VersionOverride>.Failure(game.Problem!);
        }

        return Result<VersionOverride>.Success(new VersionOverride(
            root.Id,
            version.FolderName,
            string.Equals(DisplayNameDraft, version.Descriptor.DisplayName, StringComparison.Ordinal)
                ? null
                : DisplayNameDraft,
            IsolationOverride,
            NullIfBlank(AccountIdDraft),
            NullIfBlank(JavaPathDraft),
            minimum.Value,
            maximum.Value,
            GcProfileDraft,
            jvm.Value,
            game.Value));
    }

    private Result<int?> ParseMemory(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<int?>.Success(null);
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) ||
            value < 512)
        {
            return InvalidDraft<int?>(
                "VERSION_MEMORY_INVALID",
                $"{field} 必须是至少 512 MB 的整数。",
                1);
        }

        return Result<int?>.Success(value);
    }

    private Result<IReadOnlyList<string>> ParseArguments(string text, string label)
    {
        string normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        List<string> values = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Contains('\0'))
            {
                return InvalidDraft<IReadOnlyList<string>>(
                    "VERSION_ARGUMENT_NUL",
                    $"{label}参数第 {index + 1} 行包含 NUL。",
                    index + 1);
            }

            if (Encoding.UTF8.GetByteCount(line) > MaxArgumentBytes)
            {
                return InvalidDraft<IReadOnlyList<string>>(
                    "VERSION_ARGUMENT_TOO_LONG",
                    $"{label}参数第 {index + 1} 行超过 8 KiB。",
                    index + 1);
            }

            if (label == "JVM" && IsStructuredJvmArgument(line))
            {
                return InvalidDraft<IReadOnlyList<string>>(
                    "JVM_ARGUMENT_CONFLICT",
                    $"JVM 参数第 {index + 1} 行与内存或 GC 设置冲突。",
                    index + 1);
            }

            values.Add(line);
        }

        return Result<IReadOnlyList<string>>.Success(values);
    }

    private static bool IsStructuredJvmArgument(string argument) =>
        argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) ||
        (argument.StartsWith("-XX:+Use", StringComparison.OrdinalIgnoreCase) &&
            argument.EndsWith("GC", StringComparison.OrdinalIgnoreCase));

    private static Result<Unit> ValidateGcCompatibility(
        GcProfile profile,
        int javaMajor,
        JavaArchitecture? architecture)
    {
        bool compatible = profile switch
        {
            GcProfile.Automatic or GcProfile.G1 => javaMajor >= 8,
            GcProfile.Zgc => javaMajor >= 17 &&
                architecture is JavaArchitecture.X64 or JavaArchitecture.Arm64,
            GcProfile.None => true,
            _ => false,
        };
        if (compatible)
        {
            return Result.Success();
        }

        Problem problem = Problem(
            "JVM_GC_INCOMPATIBLE",
            ProblemStage.JavaResolution,
            "所选 GC 与当前 Java 版本或架构不兼容。");
        return Result<Unit>.Failure(problem);
    }

    private static GcOptionViewModel CreateGcOption(
        GcProfile profile,
        int javaMajor,
        JavaArchitecture? architecture)
    {
        bool enabled = profile switch
        {
            GcProfile.Automatic or GcProfile.G1 => javaMajor >= 8,
            GcProfile.Zgc => javaMajor >= 17 &&
                architecture is JavaArchitecture.X64 or JavaArchitecture.Arm64,
            GcProfile.None => true,
            _ => false,
        };
        return new GcOptionViewModel(
            profile,
            profile.ToString(),
            enabled,
            enabled ? null : profile == GcProfile.Zgc
                ? "ZGC 需要已验证的 Java 17+ x64/Arm64；请先选择并探测 Java。"
                : "G1 需要 Java 8+。");
    }

    private void ClearValidation()
    {
        ValidationError = null;
        ValidationErrorCode = null;
        ValidationLineIndex = null;
        OnPropertyChanged(nameof(HasValidationLine));
        OnPropertyChanged(nameof(IsJavaSelectionInvalid));
    }

    private void SetValidation(string code, string message, int? lineIndex)
    {
        ValidationErrorCode = code;
        ValidationError = message;
        ValidationLineIndex = lineIndex;
        OnPropertyChanged(nameof(HasValidationLine));
        OnPropertyChanged(nameof(GcOptionItems));
        OnPropertyChanged(nameof(IsJavaSelectionInvalid));
    }

    private Result<T> InvalidDraft<T>(string code, string message, int? lineIndex)
    {
        SetValidation(code, message, lineIndex);
        Problem problem = Problem(code, ProblemStage.VersionResolution, message, lineIndex);
        LastProblem = problem;
        return Result<T>.Failure(problem);
    }

    private Result<VersionOverride> InvalidDraft(string code, string message, int? lineIndex) =>
        InvalidDraft<VersionOverride>(code, message, lineIndex);

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static Problem Problem(
        string code,
        ProblemStage stage,
        string message,
        int? lineIndex = null) =>
        new(
            code,
            stage,
            "problem.version.desktop",
            false,
            Guid.NewGuid().ToString("N"),
            ["action.version.review_settings"],
            lineIndex is int index
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["lineIndex"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }
                : null);

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter) => await execute();
    }
}
