using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Diagnostics;

/// <summary>
/// Builds a bounded, previewable diagnostics staging area. No archive is
/// created here and no staging path is returned to the caller.
/// </summary>
public sealed class BuildDiagnosticBundle
{
    public const int ManifestSchemaVersion = 1;
    public const int MaximumEntries = 100;
    public const int MaximumTextBytes = 10 * 1024 * 1024;
    public const long MaximumBundleBytes = 50L * 1024 * 1024;

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDiagnosticSource source;
    private readonly ILogSanitizer? sanitizer;
    private readonly string? configuredStagingDirectory;

    public BuildDiagnosticBundle(
        IDiagnosticSource? source = null,
        ILogSanitizer? sanitizer = null,
        string? stagingDirectory = null)
    {
        this.source = source ?? new FileDiagnosticSource();
        this.sanitizer = sanitizer;
        configuredStagingDirectory = stagingDirectory;
    }

    public async Task<Result<PreparedDiagnosticBundle>> PrepareAsync(
        DiagnosticBundleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LauncherVersion) ||
            string.IsNullOrWhiteSpace(request.Platform) ||
            request.LauncherVersion.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            request.Platform.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_INVALID");
        }

        try
        {
            IReadOnlyList<DiagnosticSourceEntry> supplied =
                await source.CollectAsync(request, cancellationToken);
            if (supplied is null)
            {
                return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_SOURCE_UNAVAILABLE");
            }

            string stagingRoot = ResolveStagingDirectory(request);
            EnsureNoReparsePath(stagingRoot);
            Directory.CreateDirectory(stagingRoot);
            string handleId = Guid.NewGuid().ToString("N");
            string stagingPath = Path.Combine(stagingRoot, handleId);
            Directory.CreateDirectory(stagingPath);

            BundleRedactor redactor = new(sanitizer, request.GetPrivatePathPrefixes());
            List<StagedEntry> staged = [];
            AddLauncherVersion(staged, request, redactor);

            int launcherLogNumber = 0;
            bool selectedGameLogSeen = false;
            bool crashSeen = false;
            bool settingsSeen = false;
            foreach (DiagnosticSourceEntry candidate in supplied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Enum.IsDefined(candidate.Kind))
                {
                    continue;
                }

                EnsureNoReparseIfPresent(candidate.SourcePath);
                if (IsDeniedPath(candidate.SourcePath))
                {
                    continue;
                }

                if (candidate.Kind == DiagnosticSourceKind.LauncherVersion ||
                    (candidate.Kind == DiagnosticSourceKind.SelectedGameLog && selectedGameLogSeen) ||
                    (candidate.Kind == DiagnosticSourceKind.CrashReport && crashSeen) ||
                    (candidate.Kind == DiagnosticSourceKind.Settings && settingsSeen))
                {
                    continue;
                }

                if (candidate.Kind == DiagnosticSourceKind.SelectedGameLog)
                {
                    selectedGameLogSeen = true;
                }
                else if (candidate.Kind == DiagnosticSourceKind.CrashReport)
                {
                    crashSeen = true;
                }
                else if (candidate.Kind == DiagnosticSourceKind.Settings)
                {
                    settingsSeen = true;
                }

                if (candidate.Kind == DiagnosticSourceKind.LauncherLog)
                {
                    launcherLogNumber++;
                }

                bool included = candidate.IsIncluded && candidate.Kind switch
                {
                    DiagnosticSourceKind.LauncherLog => request.IncludeLauncherLogs,
                    DiagnosticSourceKind.SelectedGameLog => request.IncludeSelectedGameLog,
                    DiagnosticSourceKind.CrashReport => request.IncludeCrashReport,
                    DiagnosticSourceKind.Settings => request.IncludeSettings,
                    _ => false,
                };

                string logicalName = GetLogicalName(candidate.Kind, launcherLogNumber);
                string? raw = await ReadCandidateAsync(candidate, cancellationToken);
                if (raw is null)
                {
                    if (candidate.IsRequired)
                    {
                        return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_SOURCE_UNAVAILABLE");
                    }

                    continue;
                }

                string sanitized = redactor.Sanitize(raw);
                byte[] bytes = Encoding.UTF8.GetBytes(sanitized);
                if (bytes.LongLength > MaximumTextBytes)
                {
                    return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
                }

                staged.Add(new StagedEntry(logicalName, bytes, included, candidate.IsRequired));
            }

            if (staged.Count + 1 > MaximumEntries)
            {
                return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
            }

            long stagedBytes = staged.Sum(static entry => entry.Bytes.LongLength);
            if (stagedBytes > MaximumBundleBytes)
            {
                return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
            }

            List<DiagnosticBundleEntry> manifestEntries = [];
            foreach (StagedEntry entry in staged)
            {
                string path = ResolveStagingFile(stagingPath, entry.LogicalName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, entry.Bytes, cancellationToken);
                manifestEntries.Add(new DiagnosticBundleEntry(
                    entry.LogicalName,
                    entry.Bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(entry.Bytes)).ToLowerInvariant(),
                    entry.IsIncluded,
                    RedactionSummary()));
            }

            DateTimeOffset createdUtc = request.CreatedUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
            DiagnosticBundleEntry manifestEntry = new(
                "manifest.json",
                0,
                string.Empty,
                true,
                "Generated from the path-free preview manifest.");
            manifestEntries.Add(manifestEntry);
            DiagnosticBundleManifest manifest = new(
                ManifestSchemaVersion,
                redactor.Sanitize(request.LauncherVersion),
                createdUtc,
                manifestEntries);

            byte[] manifestBytes = [];
            long manifestSize = 0;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                manifestEntry = manifestEntry with { Size = manifestSize };
                manifestEntries[^1] = manifestEntry;
                manifest = manifest with { Entries = manifestEntries };
                manifestBytes = SerializeManifest(manifest);
                if (manifestBytes.LongLength == manifestSize)
                {
                    break;
                }

                manifestSize = manifestBytes.LongLength;
            }

            if (manifestEntries[^1].Size != manifestBytes.LongLength)
            {
                return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_INVALID");
            }

            if (manifestBytes.LongLength > MaximumTextBytes ||
                stagedBytes + manifestBytes.LongLength > MaximumBundleBytes)
            {
                return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
            }

            string manifestPath = ResolveStagingFile(stagingPath, "manifest.json");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            DiagnosticBundlePreparedHandle handle = new(handleId);
            return Result<PreparedDiagnosticBundle>.Success(new PreparedDiagnosticBundle(manifest, handle));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BundleLimitException)
        {
            return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
        }
        catch (BundleReparsePointException)
        {
            return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_REPARSE_POINT");
        }
        catch (IOException)
        {
            return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_SOURCE_UNAVAILABLE");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_SOURCE_UNAVAILABLE");
        }
        catch (ArgumentException)
        {
            return Failure<PreparedDiagnosticBundle>("DIAGNOSTIC_BUNDLE_INVALID");
        }
    }

    public static byte[] SerializeManifest(DiagnosticBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestSerializerOptions);
    }

    private static void AddLauncherVersion(
        ICollection<StagedEntry> entries,
        DiagnosticBundleRequest request,
        BundleRedactor redactor)
    {
        string content = JsonSerializer.Serialize(
            new
            {
                launcherVersion = redactor.Sanitize(request.LauncherVersion),
                platform = redactor.Sanitize(request.Platform),
            },
            ManifestSerializerOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.LongLength > MaximumTextBytes)
        {
            throw new BundleLimitException();
        }

        entries.Add(new StagedEntry("launcher-version.json", bytes, true, true));
    }

    private static string GetLogicalName(DiagnosticSourceKind kind, int launcherLogNumber) => kind switch
    {
        DiagnosticSourceKind.LauncherLog => $"logs/launcher-{launcherLogNumber:000}.log",
        DiagnosticSourceKind.SelectedGameLog => "logs/game-selected.log",
        DiagnosticSourceKind.CrashReport => "crash-report.txt",
        DiagnosticSourceKind.Settings => "settings-redacted.json",
        _ => "entry.txt",
    };

    private static async Task<string?> ReadCandidateAsync(
        DiagnosticSourceEntry candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.Content is not null)
        {
            if (Encoding.UTF8.GetByteCount(candidate.Content) > MaximumTextBytes)
            {
                throw new BundleLimitException();
            }

            return candidate.Content;
        }

        if (string.IsNullOrWhiteSpace(candidate.SourcePath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(candidate.SourcePath);
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new BundleReparsePointException();
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length > MaximumTextBytes)
        {
            throw new BundleLimitException();
        }

        using StreamReader reader = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static void EnsureNoReparseIfPresent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return;
        }

        EnsureNoReparsePath(fullPath);
    }

    private static void EnsureNoReparsePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string remainder = fullPath[root.Length..];
        string current = root;
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new BundleReparsePointException();
            }
        }
    }

    private string ResolveStagingDirectory(DiagnosticBundleRequest request)
    {
        string? configured = request.StagingDirectory ?? configuredStagingDirectory;
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "Lacertae", "diagnostic-staging")
            : configured;
        return Path.GetFullPath(root);
    }

    private static string ResolveStagingFile(string stagingPath, string logicalName)
    {
        string normalized = logicalName.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(stagingPath, normalized));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingPath)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Diagnostic logical name escaped staging.");
        }

        return path;
    }

    private static bool IsDeniedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        string fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (fileName is "lacertae.db" or "lacertae.db-shm" or "lacertae.db-wal" ||
            fileName.Contains("secret", StringComparison.Ordinal) ||
            fileName.Contains("oauth", StringComparison.Ordinal) ||
            fileName.Contains("client", StringComparison.Ordinal) && fileName.Contains("config", StringComparison.Ordinal) ||
            fileName is ".env" or ".env.local" or "environment.txt")
        {
            return true;
        }

        string[] deniedSegments = ["/secrets/", "/saves/", "/mods/", "/resourcepacks/", "/shaderpacks/"];
        return deniedSegments.Any(normalized.Contains);
    }

    private static string RedactionSummary() =>
        "Tokens, email addresses, authentication URLs, query secrets and local paths removed.";

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Storage,
        "problem.diagnostics.bundle_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.diagnostics.review_bundle"]));

    private sealed record StagedEntry(string LogicalName, byte[] Bytes, bool IsIncluded, bool IsRequired);

    private sealed class FileDiagnosticSource : IDiagnosticSource
    {
        public Task<IReadOnlyList<DiagnosticSourceEntry>> CollectAsync(
            DiagnosticBundleRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<DiagnosticSourceEntry> entries = [];
            if (request.LauncherLogContent is not null)
            {
                entries.Add(new DiagnosticSourceEntry(
                    DiagnosticSourceKind.LauncherLog,
                    content: request.LauncherLogContent,
                    isIncluded: request.IncludeLauncherLogs));
            }

            IEnumerable<string> launcherPaths = request.LauncherLogPaths
                .Concat(request.LauncherLogPath is null ? [] : [request.LauncherLogPath])
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            entries.AddRange(launcherPaths.Select(path => new DiagnosticSourceEntry(
                DiagnosticSourceKind.LauncherLog,
                sourcePath: path,
                isIncluded: request.IncludeLauncherLogs)));

            if (request.SelectedGameLogContent is not null || request.SelectedGameLogPath is not null)
            {
                entries.Add(new DiagnosticSourceEntry(
                    DiagnosticSourceKind.SelectedGameLog,
                    request.SelectedGameLogPath,
                    request.SelectedGameLogContent,
                    isIncluded: request.IncludeSelectedGameLog));
            }

            if (request.CrashReportContent is not null || request.CrashReportPath is not null)
            {
                entries.Add(new DiagnosticSourceEntry(
                    DiagnosticSourceKind.CrashReport,
                    request.CrashReportPath,
                    request.CrashReportContent,
                    isIncluded: request.IncludeCrashReport));
            }

            if (request.SettingsContent is not null || request.SettingsPath is not null)
            {
                entries.Add(new DiagnosticSourceEntry(
                    DiagnosticSourceKind.Settings,
                    request.SettingsPath,
                    request.SettingsContent,
                    isIncluded: request.IncludeSettings));
            }

            entries.AddRange(request.AdditionalEntries ?? []);
            return Task.FromResult<IReadOnlyList<DiagnosticSourceEntry>>(entries);
        }
    }

    private sealed class BundleRedactor
    {
        private static readonly Regex EmailRegex = new(
            @"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex AuthUrlRegex = new(
            """(?i)https?://[^\s"'<>]*(?:auth|oauth|login|authorize|token)[^\s"'<>]*""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SensitiveQueryRegex = new(
            @"(?i)([?&](?:code|state|access_token|refresh_token|id_token|token|client_id|client_secret|redirect_uri)=)[^&\s]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex BearerRegex = new(
            @"(?i)(\bBearer\s+)[^\s]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SensitiveKeyRegex = new(
            """(?i)("?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|client[_-]?id|authorization|password)"?\s*[:=]\s*"?)[^"\s,}&]+""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex WindowsHomeRegex = new(
            """(?i)[A-Z]:[\\/]Users[\\/][^\\/\s]+(?:[\\/][^\s"']*)?""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UnixHomeRegex = new(
            """(?<![A-Za-z0-9])/(?:home|Users)/[^\s"']+""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly ILogSanitizer? sanitizer;
        private readonly string[] prefixes;

        public BundleRedactor(ILogSanitizer? sanitizer, IEnumerable<string> prefixes)
        {
            this.sanitizer = sanitizer;
            this.prefixes = prefixes
                .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
                .OrderByDescending(static prefix => prefix.Length)
                .ToArray();
        }

        public string Sanitize(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            string result = sanitizer?.Sanitize(value) ?? value;
            foreach (string prefix in prefixes)
            {
                result = result.Replace(prefix, "%LOCAL_PATH%", StringComparison.OrdinalIgnoreCase);
                result = result.Replace(prefix.Replace('\\', '/'), "%LOCAL_PATH%", StringComparison.OrdinalIgnoreCase);
            }

            result = BearerRegex.Replace(result, "$1[REDACTED]");
            result = SensitiveQueryRegex.Replace(result, "$1[REDACTED]");
            result = SensitiveKeyRegex.Replace(result, "$1[REDACTED]");
            result = AuthUrlRegex.Replace(result, "[REDACTED_AUTH_URL]");
            result = EmailRegex.Replace(result, "[REDACTED_EMAIL]");
            result = WindowsHomeRegex.Replace(result, "%USERPROFILE%");
            result = UnixHomeRegex.Replace(result, "%USERPROFILE%");
            return result;
        }
    }

    private sealed class BundleLimitException : Exception;

    private sealed class BundleReparsePointException : Exception;
}
