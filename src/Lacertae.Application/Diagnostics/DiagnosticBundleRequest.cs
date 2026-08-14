using Lacertae.Domain.Diagnostics;

namespace Lacertae.Application.Diagnostics;

/// <summary>
/// Kinds of local data that the diagnostic exporter is allowed to handle.
/// Anything outside this allow-list is ignored by the application layer.
/// </summary>
public enum DiagnosticSourceKind
{
    LauncherVersion,
    LauncherLog,
    SelectedGameLog,
    CrashReport,
    Settings,
}

/// <summary>
/// A candidate supplied by an <see cref="IDiagnosticSource"/>. Source paths
/// are consumed only while preparing the private staging area and are never
/// copied into the preview manifest.
/// </summary>
public sealed record DiagnosticSourceEntry
{
    public DiagnosticSourceEntry(
        DiagnosticSourceKind kind,
        string? sourcePath = null,
        string? content = null,
        bool isRequired = false,
        bool isIncluded = true,
        string? logicalName = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown diagnostic source kind.");
        }

        Kind = kind;
        SourcePath = sourcePath;
        Content = content;
        IsRequired = isRequired;
        IsIncluded = isIncluded;
        LogicalName = logicalName;
    }

    public DiagnosticSourceKind Kind { get; init; }

    public string? SourcePath { get; init; }

    public string? Content { get; init; }

    public bool IsRequired { get; init; }

    public bool IsIncluded { get; init; }

    /// <summary>
    /// Optional source label. It is never used as a ZIP name; the application
    /// assigns a fixed, path-free logical name based on <see cref="Kind"/>.
    /// </summary>
    public string? LogicalName { get; init; }
}

/// <summary>
/// Inputs for a diagnostic preview. All paths are local hints used only while
/// staging. The resulting manifest contains no path values.
/// </summary>
public sealed record DiagnosticBundleRequest
{
    public DiagnosticBundleRequest()
    {
    }

    public DiagnosticBundleRequest(
        string launcherVersion,
        string platform,
        string? launcherLogPath = null,
        string? selectedGameLogPath = null,
        string? crashReportPath = null,
        string? settingsPath = null,
        string? stagingDirectory = null)
    {
        LauncherVersion = launcherVersion;
        Platform = platform;
        LauncherLogPath = launcherLogPath;
        SelectedGameLogPath = selectedGameLogPath;
        CrashReportPath = crashReportPath;
        SettingsPath = settingsPath;
        StagingDirectory = stagingDirectory;
    }

    public string LauncherVersion { get; init; } = "unknown";

    public string Platform { get; init; } = "unknown";

    public string? LauncherLogPath { get; init; }

    public IReadOnlyList<string> LauncherLogPaths { get; init; } = [];

    public string? SelectedGameLogPath { get; init; }

    public string? GameLogPath
    {
        get => SelectedGameLogPath;
        init => SelectedGameLogPath = value;
    }

    public string? CrashReportPath { get; init; }

    public string? SettingsPath { get; init; }

    public string? LauncherLogContent { get; init; }

    public string? SelectedGameLogContent { get; init; }

    public string? CrashReportContent { get; init; }

    public string? SettingsContent { get; init; }

    public string? DataRootPath { get; init; }

    public string? GameRootPath { get; init; }

    public string? UserProfilePath { get; init; }

    public IReadOnlyList<string> PrivatePathPrefixes { get; init; } = [];

    public IReadOnlyList<DiagnosticSourceEntry> AdditionalEntries { get; init; } = [];

    public string? StagingDirectory { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public bool IncludeLauncherLogs { get; init; } = true;

    public bool IncludeSelectedGameLog { get; init; } = true;

    public bool IncludeCrashReport { get; init; } = true;

    public bool IncludeSettings { get; init; } = true;

    /// <summary>
    /// Returns all explicitly provided private path prefixes. The launcher
    /// data and game roots are included automatically when present.
    /// </summary>
    public IReadOnlyList<string> GetPrivatePathPrefixes() =>
        PrivatePathPrefixes
            .Concat([DataRootPath, GameRootPath, UserProfilePath])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Result of preparing a bundle. The handle is intentionally opaque; callers
/// must pass it back to the ZIP writer instead of receiving a staging path.
/// </summary>
public sealed record PreparedDiagnosticBundle(
    DiagnosticBundleManifest Manifest,
    DiagnosticBundlePreparedHandle Handle);
