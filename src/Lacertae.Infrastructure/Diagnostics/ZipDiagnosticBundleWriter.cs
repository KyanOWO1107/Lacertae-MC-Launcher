using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lacertae.Application.Diagnostics;
using Lacertae.Application.Storage;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Diagnostics;

/// <summary>
/// Writes a previously prepared diagnostic staging area to a ZIP archive.
/// The writer never accepts arbitrary source paths and requires explicit user
/// confirmation before creating the destination file.
/// </summary>
public sealed class ZipDiagnosticBundleWriter
{
    private static readonly DateTimeOffset DeterministicTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long MaximumBundleBytes = BuildDiagnosticBundle.MaximumBundleBytes;
    private const int MaximumTextBytes = BuildDiagnosticBundle.MaximumTextBytes;
    private const int MaximumEntries = BuildDiagnosticBundle.MaximumEntries;
    private static readonly Regex SafeLogicalNameRegex = new(
        "^[a-z0-9][a-z0-9._/-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EmailRegex = new(
        "(?i)\\b[A-Z0-9._%+\\-]+@[A-Z0-9.\\-]+\\.[A-Z]{2,}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AuthUrlRegex = new(
        "(?i)https?://[^\\s\\\"'<>]*(?:auth|oauth|login|authorize|token)[^\\s\\\"'<>]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string stagingRoot;

    public ZipDiagnosticBundleWriter(string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            throw new ArgumentException("A staging directory is required.", nameof(stagingRoot));
        }

        this.stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
    }

    public Task<Result<string>> WriteAsync(
        PreparedDiagnosticBundle prepared,
        string destinationPath,
        bool confirmed,
        CancellationToken cancellationToken) =>
        prepared is null
            ? throw new ArgumentNullException(nameof(prepared))
            : WriteAsync(prepared.Handle, prepared.Manifest, destinationPath, confirmed, cancellationToken);

    public async Task<Result<string>> WriteAsync(
        DiagnosticBundlePreparedHandle handle,
        DiagnosticBundleManifest manifest,
        string destinationPath,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!confirmed)
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_CONFIRMATION_REQUIRED");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_INVALID");
        }

        try
        {
            string stagingPath = ResolveHandlePath(handle);
            using IDisposable stagingLease = SecureFileSystem.OpenDirectoryLease(stagingPath, stagingRoot);
            ValidateStagingDirectory(stagingPath);
            string outputPath = Path.GetFullPath(destinationPath);
            string outputParent = Path.GetDirectoryName(outputPath)!;
            using IDisposable outputLease = SecureFileSystem.OpenDirectoryLease(outputParent);
            ValidateDestinationPath(outputPath);

            DiagnosticBundleManifest safeManifest = ValidateManifest(manifest);
            DiagnosticBundleEntry[] includedEntries = safeManifest.Entries
                .Where(static entry => entry.IsIncluded && !entry.LogicalName.Equals("manifest.json", StringComparison.Ordinal))
                .OrderBy(static entry => entry.LogicalName, StringComparer.Ordinal)
                .ToArray();
            if (includedEntries.Length + 1 > MaximumEntries)
            {
                return Failure<string>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
            }

            long contentBytes = 0;
            foreach (DiagnosticBundleEntry entry in includedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = ResolveStagingFile(stagingPath, entry.LogicalName);
                ValidateStagingFile(stagingPath, sourcePath);
                long entryBytes;
                await using (Stream hashStream = SecureFileSystem.OpenRead(sourcePath, stagingPath))
                {
                    entryBytes = hashStream.Length;
                    if (entryBytes > MaximumTextBytes || entry.Size != entryBytes)
                    {
                        return Failure<string>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
                    }

                    string actualHash = Convert.ToHexString(
                        await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
                    if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure<string>("DIAGNOSTIC_BUNDLE_INVALID");
                    }
                }

                contentBytes = checked(contentBytes + entryBytes);
                if (contentBytes > MaximumBundleBytes)
                {
                    return Failure<string>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
                }
            }

            byte[] manifestBytes = BuildDiagnosticBundle.SerializeManifest(safeManifest);
            if (manifestBytes.LongLength > MaximumTextBytes || contentBytes + manifestBytes.LongLength > MaximumBundleBytes)
            {
                return Failure<string>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
            }

            string temporaryPath = outputPath + ".part";
            SecureFileSystem.DeleteFile(temporaryPath, outputParent);

            try
            {
                await using (Stream stream = SecureFileSystem.OpenWrite(temporaryPath, FileMode.CreateNew, outputParent))
                using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
                {
                    foreach (DiagnosticBundleEntry entry in includedEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddFileEntry(archive, stagingPath, entry);
                    }

                    ZipArchiveEntry manifestArchiveEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    manifestArchiveEntry.LastWriteTime = DeterministicTimestamp;
                    await using Stream manifestStream = manifestArchiveEntry.Open();
                    await manifestStream.WriteAsync(manifestBytes, cancellationToken);
                }

                SecureFileSystem.MoveReplace(temporaryPath, outputPath, outputParent);
                if (!SecureFileSystem.IsSafeFile(outputPath, outputParent))
                {
                    throw new DiagnosticBundleReparsePointException();
                }

                return Result<string>.Success(outputPath);
            }
            finally
            {
                SecureFileSystem.DeleteFile(temporaryPath, outputParent);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiagnosticBundleReparsePointException)
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_REPARSE_POINT");
        }
        catch (DiagnosticBundleInvalidException)
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_INVALID");
        }
        catch (IOException)
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_WRITE_FAILED");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_WRITE_FAILED");
        }
        catch (OverflowException)
        {
            return Failure<string>("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED");
        }
    }

    private static void AddFileEntry(
        ZipArchive archive,
        string stagingPath,
        DiagnosticBundleEntry entry)
    {
        string sourcePath = ResolveStagingFile(stagingPath, entry.LogicalName);
        ValidateStagingFile(stagingPath, sourcePath);
        ZipArchiveEntry archiveEntry = archive.CreateEntry(entry.LogicalName, CompressionLevel.Optimal);
        archiveEntry.LastWriteTime = DeterministicTimestamp;
        using Stream source = SecureFileSystem.OpenRead(sourcePath, stagingPath);
        using Stream destination = archiveEntry.Open();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total = checked(total + read);
            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (total != entry.Size || !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiagnosticBundleInvalidException();
        }
    }

    private static DiagnosticBundleManifest ValidateManifest(DiagnosticBundleManifest manifest)
    {
        if (manifest.SchemaVersion != BuildDiagnosticBundle.ManifestSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.LauncherVersion) ||
            manifest.Entries is null ||
            manifest.Entries.Count is < 1 or > MaximumEntries ||
            manifest.Entries.Any(static entry => entry is null))
        {
            throw new DiagnosticBundleInvalidException();
        }

        if (ContainsSensitive(manifest.LauncherVersion))
        {
            throw new DiagnosticBundleInvalidException();
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (DiagnosticBundleEntry entry in manifest.Entries)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.LogicalName) ||
                !SafeLogicalNameRegex.IsMatch(entry.LogicalName) ||
                entry.LogicalName.StartsWith('/') ||
                entry.LogicalName.Contains("..", StringComparison.Ordinal) ||
                !names.Add(entry.LogicalName) ||
                entry.Size < 0 ||
                entry.Size > MaximumTextBytes ||
                string.IsNullOrWhiteSpace(entry.Sha256) && !entry.LogicalName.Equals("manifest.json", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.RedactionSummary) ||
                ContainsSensitive(entry.LogicalName) ||
                ContainsSensitive(entry.RedactionSummary) ||
                (!entry.LogicalName.Equals("manifest.json", StringComparison.Ordinal) &&
                    (entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))))
            {
                throw new DiagnosticBundleInvalidException();
            }
        }

        DiagnosticBundleEntry? launcher = manifest.Entries.FirstOrDefault(static entry =>
            entry.LogicalName.Equals("launcher-version.json", StringComparison.Ordinal));
        DiagnosticBundleEntry? manifestEntry = manifest.Entries.FirstOrDefault(static entry =>
            entry.LogicalName.Equals("manifest.json", StringComparison.Ordinal));
        if (launcher is null || !launcher.IsIncluded || manifestEntry is null || !manifestEntry.IsIncluded)
        {
            throw new DiagnosticBundleInvalidException();
        }

        // The manifest carries its own byte length so a writer cannot silently
        // serialize a different document than the one shown in the preview.
        // This check is intentionally performed after all entry validation and
        // uses the exact serializer used for the archive payload.
        byte[] serializedManifest = BuildDiagnosticBundle.SerializeManifest(manifest);
        if (manifestEntry.Size != serializedManifest.LongLength)
        {
            throw new DiagnosticBundleInvalidException();
        }

        return manifest;
    }

    private string ResolveHandlePath(DiagnosticBundlePreparedHandle handle)
    {
        if (string.IsNullOrWhiteSpace(handle.Id) ||
            handle.Id.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new DiagnosticBundleInvalidException();
        }

        string path = Path.GetFullPath(Path.Combine(stagingRoot, handle.Id));
        string prefix = stagingRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new DiagnosticBundleInvalidException();
        }

        return path;
    }

    private void ValidateStagingDirectory(string path)
    {
        ValidateDirectoryChain(stagingRoot);
        ValidateDirectoryChain(path, stagingRoot);
    }

    private static void ValidateDestinationPath(string outputPath)
    {
        string? parent = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new DiagnosticBundleInvalidException();
        }

        ValidateDirectoryChain(parent);
        if (File.Exists(outputPath))
        {
            ValidateRegularFile(outputPath, parent);
        }
        else if (Directory.Exists(outputPath))
        {
            throw new DiagnosticBundleInvalidException();
        }

        string temporaryPath = outputPath + ".part";
        if (File.Exists(temporaryPath))
        {
            ValidateRegularFile(temporaryPath, parent);
        }
        else if (Directory.Exists(temporaryPath))
        {
            throw new DiagnosticBundleInvalidException();
        }
    }

    private static string ResolveStagingFile(string stagingPath, string logicalName)
    {
        string path = Path.GetFullPath(Path.Combine(stagingPath, logicalName.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingPath)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new DiagnosticBundleInvalidException();
        }

        return path;
    }

    private static void ValidateRegularDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DiagnosticBundleInvalidException();
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new DiagnosticBundleReparsePointException();
        }
    }

    private static void ValidateStagingFile(string stagingPath, string path)
    {
        ValidateDirectoryChain(Path.GetDirectoryName(path)!, stagingPath);
        ValidateRegularFile(path, stagingPath);
    }

    private static void ValidateDirectoryChain(string path, string? allowedRoot = null)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root = allowedRoot is null
            ? Path.GetPathRoot(fullPath)
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new DiagnosticBundleInvalidException();
        }

        if (allowedRoot is not null)
        {
            string prefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                !fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new DiagnosticBundleInvalidException();
            }
        }

        string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        string relative = fullPath[pathRoot.Length..];
        string current = pathRoot;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                throw new DiagnosticBundleInvalidException();
            }

            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DiagnosticBundleReparsePointException();
            }
        }
    }

    private static void ValidateRegularFile(string path, string root)
    {
        if (!SecureFileSystem.IsSafeFile(path, root))
        {
            throw new DiagnosticBundleInvalidException();
        }
    }

    private static bool ContainsSensitive(string value) =>
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        SensitiveQueryRegex.IsMatch(value) ||
        SensitiveKeyRegex.IsMatch(value) ||
        EmailRegex.IsMatch(value) ||
        AuthUrlRegex.IsMatch(value) ||
        WindowsHomeRegex.IsMatch(value) ||
        UnixHomeRegex.IsMatch(value);

    private static readonly Regex SensitiveQueryRegex = new(
        "(?i)[?&](?:code|state|access[_-]?token|refresh[_-]?token|id[_-]?token|token|client[_-]?id|client[_-]?secret|redirect[_-]?uri|auth[_-]?code|code[_-]?verifier)=[^&\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveKeyRegex = new(
        "(?i)\\\"?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|client[_-]?id|authorization|password)\\\"?\\s*[:=]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsHomeRegex = new(
        "(?i)\\b[A-Z]:[\\\\/](?:Users|Documents and Settings)[\\\\/][^\\s\\\"']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnixHomeRegex = new(
        "(?<![A-Za-z0-9])/(?:home|Users)/[^\\s\\\"']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Storage,
        "problem.diagnostics.bundle_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.diagnostics.review_bundle"]));

    private sealed class DiagnosticBundleReparsePointException : Exception;

    private sealed class DiagnosticBundleInvalidException : Exception;
}
