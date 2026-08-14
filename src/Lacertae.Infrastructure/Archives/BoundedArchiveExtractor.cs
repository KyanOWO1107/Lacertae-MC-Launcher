using System.Formats.Tar;
using System.IO.Compression;
using Lacertae.Application.Archives;
using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Archives;

public sealed class BoundedArchiveExtractor : IArchiveExtractor
{
    private const int BufferSize = 128 * 1024;

    public async Task<Result<Unit>> ExtractAsync(
        ArchiveExtractionRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        if (!IsValidRequest(request))
        {
            return Result<Unit>.Failure(Problem("ARCHIVE_REQUEST_INVALID"));
        }

        string archivePath;
        string destination;
        bool destinationExisted;
        List<string> createdFiles = [];
        List<string> createdDirectories = [];
        try
        {
            archivePath = Path.GetFullPath(request.ArchivePath);
            destination = Path.GetFullPath(request.DestinationDirectory);
            destinationExisted = Directory.Exists(destination);
            ValidateArchivePath(archivePath);
            EnsureDestination(destination);
            if (!destinationExisted)
            {
                createdDirectories.Add(destination);
            }

            string extension = GetArchiveExtension(archivePath);
            HashSet<string> explicitEntries = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
            ExtractionState state = new();

            if (extension == ".zip")
            {
                await ExtractZipAsync(
                    archivePath,
                    destination,
                    request,
                    progress,
                    explicitEntries,
                    files,
                    directories,
                    createdFiles,
                    createdDirectories,
                    state,
                    cancellationToken);
            }
            else if (extension == ".tar")
            {
                await ExtractTarAsync(
                    archivePath,
                    destination,
                    request,
                    progress,
                    explicitEntries,
                    files,
                    directories,
                    createdFiles,
                    createdDirectories,
                    state,
                    cancellationToken);
            }
            else
            {
                throw new ArchiveExtractionException("ARCHIVE_FORMAT_UNSUPPORTED");
            }

            progress.Report(new OperationProgress(
                "extract",
                state.Entries,
                state.Entries,
                state.CompletedBytes,
                request.MaximumExpandedBytes));
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            Cleanup(createdFiles, createdDirectories);
            throw;
        }
        catch (ArchiveExtractionException exception)
        {
            Cleanup(createdFiles, createdDirectories);
            return Result<Unit>.Failure(Problem(exception.Code));
        }
        catch (InvalidDataException)
        {
            Cleanup(createdFiles, createdDirectories);
            return Result<Unit>.Failure(Problem("ARCHIVE_FORMAT_INVALID"));
        }
        catch (EndOfStreamException)
        {
            Cleanup(createdFiles, createdDirectories);
            return Result<Unit>.Failure(Problem("ARCHIVE_FORMAT_INVALID"));
        }
        catch (OverflowException)
        {
            Cleanup(createdFiles, createdDirectories);
            return Result<Unit>.Failure(Problem("ARCHIVE_LIMIT_EXCEEDED"));
        }
        catch (IOException)
        {
            Cleanup(createdFiles, createdDirectories);
            return Result<Unit>.Failure(Problem("ARCHIVE_EXTRACTION_FAILED", retryable: true));
        }
        catch (UnauthorizedAccessException)
        {
            Cleanup(createdFiles, createdDirectories);
            return Result<Unit>.Failure(Problem("ARCHIVE_EXTRACTION_FAILED"));
        }
    }

    private static async Task ExtractZipAsync(
        string archivePath,
        string destination,
        ArchiveExtractionRequest request,
        IProgress<OperationProgress> progress,
        HashSet<string> explicitEntries,
        HashSet<string> files,
        HashSet<string> directories,
        List<string> createdFiles,
        List<string> createdDirectories,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isDirectory = entry.FullName.EndsWith('/') || entry.Name.Length == 0;
            if (IsZipLink(entry))
            {
                throw new ArchiveExtractionException("ARCHIVE_LINK_NOT_ALLOWED");
            }

            string relativePath = NormalizeEntryPath(entry.FullName, isDirectory);
            ValidateEntryIdentity(relativePath, isDirectory, explicitEntries, files, directories);
            ValidateEntrySize(entry.Length, entry.CompressedLength, request, state);
            if (isDirectory)
            {
                EnsureDirectory(destination, relativePath, directories, createdDirectories);
            }
            else
            {
                await ExtractFileAsync(
                    entry.Open(),
                    destination,
                    relativePath,
                    entry.Length,
                    request,
                    progress,
                    files,
                    directories,
                    createdFiles,
                    createdDirectories,
                    state,
                    cancellationToken);
            }

            state.Entries = checked(state.Entries + 1);
            if (state.Entries > request.MaximumEntries)
            {
                throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
            }

            explicitEntries.Add(relativePath);
            progress.Report(new OperationProgress(
                "extract",
                state.Entries,
                request.MaximumEntries,
                state.CompletedBytes,
                request.MaximumExpandedBytes));
        }
    }

    private static async Task ExtractTarAsync(
        string archivePath,
        string destination,
        ArchiveExtractionRequest request,
        IProgress<OperationProgress> progress,
        HashSet<string> explicitEntries,
        HashSet<string> files,
        HashSet<string> directories,
        List<string> createdFiles,
        List<string> createdDirectories,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using TarReader reader = new(stream, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isDirectory = entry.EntryType == TarEntryType.Directory;
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                throw new ArchiveExtractionException("ARCHIVE_LINK_NOT_ALLOWED");
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile or TarEntryType.Directory))
            {
                throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
            }

            string relativePath = NormalizeEntryPath(entry.Name, isDirectory);
            ValidateEntryIdentity(relativePath, isDirectory, explicitEntries, files, directories);
            long declaredSize = isDirectory ? 0 : entry.Length;
            ValidateEntrySize(declaredSize, declaredSize, request, state);
            if (isDirectory)
            {
                EnsureDirectory(destination, relativePath, directories, createdDirectories);
            }
            else
            {
                if (entry.DataStream is null)
                {
                    throw new ArchiveExtractionException("ARCHIVE_FORMAT_INVALID");
                }

                await ExtractFileAsync(
                    entry.DataStream,
                    destination,
                    relativePath,
                    declaredSize,
                    request,
                    progress,
                    files,
                    directories,
                    createdFiles,
                    createdDirectories,
                    state,
                    cancellationToken);
            }

            state.Entries = checked(state.Entries + 1);
            if (state.Entries > request.MaximumEntries)
            {
                throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
            }

            explicitEntries.Add(relativePath);
            progress.Report(new OperationProgress(
                "extract",
                state.Entries,
                request.MaximumEntries,
                state.CompletedBytes,
                request.MaximumExpandedBytes));
        }
    }

    private static async Task ExtractFileAsync(
        Stream input,
        string destination,
        string relativePath,
        long declaredSize,
        ArchiveExtractionRequest request,
        IProgress<OperationProgress> progress,
        HashSet<string> files,
        HashSet<string> directories,
        List<string> createdFiles,
        List<string> createdDirectories,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        await using Stream source = input;
        string target = ResolveTarget(destination, relativePath);
        EnsureParentDirectories(target, destination, directories, createdDirectories);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_CONFLICT");
        }

        await using FileStream output = new(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        createdFiles.Add(target);
        long written = 0;
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            written = checked(written + read);
            if (written > declaredSize || written > request.MaximumExpandedBytes)
            {
                throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
            }

            state.CompletedBytes = checked(state.CompletedBytes + read);
            if (state.CompletedBytes > request.MaximumExpandedBytes)
            {
                throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress.Report(new OperationProgress(
                "extract",
                state.Entries,
                request.MaximumEntries,
                state.CompletedBytes,
                request.MaximumExpandedBytes));
        }

        if (written != declaredSize)
        {
            throw new ArchiveExtractionException("ARCHIVE_FORMAT_INVALID");
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(true);
        files.Add(relativePath);
    }

    private static void EnsureDirectory(
        string destination,
        string relativePath,
        HashSet<string> directories,
        List<string> createdDirectories)
    {
        string target = ResolveTarget(destination, relativePath);
        EnsureParentDirectories(target, destination, directories, createdDirectories, includeTarget: true);
        if (File.Exists(target))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_CONFLICT");
        }

        if (Directory.Exists(target))
        {
            EnsureNotReparse(target, destination);
            directories.Add(relativePath);
            return;
        }

        Directory.CreateDirectory(target);
        EnsureNotReparse(target, destination);
        createdDirectories.Add(target);
        directories.Add(relativePath);
    }

    private static void EnsureParentDirectories(
        string target,
        string destination,
        HashSet<string> directories,
        List<string> createdDirectories,
        bool includeTarget = false)
    {
        string? parent = includeTarget ? target : Path.GetDirectoryName(target);
        if (parent is null)
        {
            throw new ArchiveExtractionException("ARCHIVE_PATH_INVALID");
        }

        string relativeParent = Path.GetRelativePath(destination, parent).Replace(Path.DirectorySeparatorChar, '/');
        if (relativeParent == ".")
        {
            return;
        }

        string current = destination;
        foreach (string segment in relativeParent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparse(current, destination);
            if (File.Exists(current))
            {
                throw new ArchiveExtractionException("ARCHIVE_ENTRY_CONFLICT");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
                EnsureNotReparse(current, destination);
                createdDirectories.Add(current);
            }

            directories.Add(Path.GetRelativePath(destination, current).Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    private static void ValidateEntryIdentity(
        string relativePath,
        bool isDirectory,
        HashSet<string> explicitEntries,
        HashSet<string> files,
        HashSet<string> directories)
    {
        if (explicitEntries.Contains(relativePath) || (isDirectory ? files : directories).Contains(relativePath))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_CONFLICT");
        }

        string[] segments = relativePath.Split('/');
        string current = string.Empty;
        for (int index = 0; index < segments.Length - (isDirectory ? 0 : 1); index++)
        {
            current = current.Length == 0 ? segments[index] : current + "/" + segments[index];
            if (files.Contains(current))
            {
                throw new ArchiveExtractionException("ARCHIVE_ENTRY_CONFLICT");
            }
        }
    }

    private static void ValidateEntrySize(
        long expandedSize,
        long compressedSize,
        ArchiveExtractionRequest request,
        ExtractionState state)
    {
        if (expandedSize < 0 || compressedSize < 0 || expandedSize > request.MaximumExpandedBytes)
        {
            throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
        }

        state.TotalExpandedBytes = checked(state.TotalExpandedBytes + expandedSize);
        if (state.TotalExpandedBytes > request.MaximumExpandedBytes)
        {
            throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
        }

        if (expandedSize > 0 && (compressedSize == 0 || expandedSize > checked(compressedSize * (long)request.MaximumExpansionRatio)))
        {
            throw new ArchiveExtractionException("ARCHIVE_LIMIT_EXCEEDED");
        }
    }

    private static string NormalizeEntryPath(string rawPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
        }

        string normalized = rawPath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains('\0') || normalized.Contains("//", StringComparison.Ordinal))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
        }

        if (isDirectory)
        {
            normalized = normalized.TrimEnd('/');
        }

        if (normalized.Length == 0 || normalized.EndsWith('/'))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            ValidateSegment(segment);
        }

        string fullPath = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar));
        if (Path.IsPathRooted(fullPath) && !string.Equals(fullPath, normalized, StringComparison.Ordinal))
        {
            // The relative check below is authoritative; this branch documents that
            // platform-specific rooted-path semantics are intentionally not trusted.
        }

        return string.Join('/', segments);
    }

    private static void ValidateSegment(string segment)
    {
        if (segment is "." or ".." || segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':') ||
            segment.Any(character => char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
        }

        string withoutExtension = segment.Split('.', 2)[0];
        if (withoutExtension.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            withoutExtension.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            withoutExtension.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            withoutExtension.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (withoutExtension.Length == 4 && (withoutExtension.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || withoutExtension.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(withoutExtension[3])))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
        }
    }

    private static string ResolveTarget(string destination, string relativePath)
    {
        string target = Path.GetFullPath(Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(target, destination))
        {
            throw new ArchiveExtractionException("ARCHIVE_ENTRY_INVALID");
        }

        EnsureNotReparse(target, destination);
        return target;
    }

    private static void ValidateArchivePath(string archivePath)
    {
        if (!File.Exists(archivePath) || (File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArchiveExtractionException("ARCHIVE_REQUEST_INVALID");
        }
    }

    private static void EnsureDestination(string destination)
    {
        string? parent = Directory.GetParent(destination)?.FullName;
        if (parent is null)
        {
            throw new ArchiveExtractionException("ARCHIVE_REQUEST_INVALID");
        }

        Directory.CreateDirectory(destination);
        EnsureNotReparse(destination, parent);
    }

    private static void EnsureNotReparse(string path, string root)
    {
        if (HasReparsePointBetween(path, root))
        {
            throw new ArchiveExtractionException("ARCHIVE_REPARSE_POINT");
        }
    }

    private static bool HasReparsePointBetween(string path, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? current = File.Exists(path) || Directory.Exists(path)
            ? Path.GetFullPath(path)
            : Path.GetDirectoryName(Path.GetFullPath(path));
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            current = string.Equals(parent, current, StringComparison.OrdinalIgnoreCase) ? null : parent;
        }

        return true;
    }

    private static bool IsZipLink(ZipArchiveEntry entry)
    {
        uint attributes = unchecked((uint)entry.ExternalAttributes);
        return ((attributes >> 16) & 0xF000) == 0xA000;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetArchiveExtension(string archivePath) =>
        Path.GetExtension(archivePath).ToLowerInvariant();

    private static bool IsValidRequest(ArchiveExtractionRequest request) =>
        !string.IsNullOrWhiteSpace(request.ArchivePath) &&
        !string.IsNullOrWhiteSpace(request.DestinationDirectory) &&
        request.MaximumEntries > 0 &&
        request.MaximumExpandedBytes > 0 &&
        request.MaximumExpansionRatio > 0;

    private static void Cleanup(List<string> files, List<string> directories)
    {
        foreach (string path in files.OrderByDescending(static path => path.Length))
        {
            TryDeleteFile(path);
        }

        foreach (string path in directories
                     .OrderByDescending(static path => path.Length)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path) && !File.Exists(path))
                {
                    Directory.Delete(path, recursive: false);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Problem Problem(string code, bool retryable = false) => new(
        code,
        ProblemStage.Configuration,
        code switch
        {
            "ARCHIVE_ENTRY_INVALID" => "problem.archive.entry_invalid",
            "ARCHIVE_ENTRY_CONFLICT" => "problem.archive.entry_conflict",
            "ARCHIVE_LINK_NOT_ALLOWED" => "problem.archive.link_not_allowed",
            "ARCHIVE_LIMIT_EXCEEDED" => "problem.archive.limit_exceeded",
            "ARCHIVE_REPARSE_POINT" => "problem.archive.reparse_point",
            "ARCHIVE_FORMAT_INVALID" => "problem.archive.format_invalid",
            "ARCHIVE_FORMAT_UNSUPPORTED" => "problem.archive.format_unsupported",
            _ => "problem.archive.extraction_failed",
        },
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.archive.retry"]);

    private sealed class ExtractionState
    {
        public int Entries { get; set; }
        public long TotalExpandedBytes { get; set; }
        public long CompletedBytes { get; set; }
    }

    private sealed class ArchiveExtractionException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
