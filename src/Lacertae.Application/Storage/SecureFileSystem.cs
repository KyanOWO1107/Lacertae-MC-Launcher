using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CA1838

namespace Lacertae.Application.Storage;

/// <summary>
/// Performs launcher-owned file operations while binding the path to the
/// directory objects that were inspected. On Windows, each existing directory
/// in the path is held open without delete sharing and every opened object is
/// checked with an open-reparse-point handle. This closes the check/use window
/// that a sequence of File.Exists/File.Move calls would otherwise leave.
/// </summary>
public static class SecureFileSystem
{
    private const int BufferSize = 64 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint CreateAlways = 2;
    private const uint OpenAlways = 4;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const uint ReparsePointAttribute = 0x00000400;

    public static void EnsureDirectory(string path, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        if (allowedRoot is not null && !IsUnderRoot(fullPath, allowedRoot))
        {
            throw new IOException("A secure directory path escaped its allowed root.");
        }

        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            throw new IOException("A secure directory path has no root.");
        }

        string current = pathRoot;
        foreach (string segment in fullPath[pathRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            if (!Directory.Exists(next))
            {
                string? leaseRoot = allowedRoot is not null && IsUnderRoot(current, allowedRoot) ? allowedRoot : null;
                using DirectoryLease parentLease = AcquireDirectoryLease(current, leaseRoot);
                Directory.CreateDirectory(next);
            }

            string? createdRoot = allowedRoot is not null && IsUnderRoot(next, allowedRoot) ? allowedRoot : null;
            using DirectoryLease createdLease = AcquireDirectoryLease(next, createdRoot);
            current = next;
        }
    }

    public static Stream OpenRead(string path, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        return OpenFile(fullPath, FileMode.Open, FileAccess.Read, allowedRoot);
    }

    /// <summary>
    /// Opens a regular file while denying other processes write and delete
    /// sharing. This is used at process-start boundaries where a path check
    /// must remain bound to the executable object until the OS has consumed it.
    /// </summary>
    public static Stream OpenReadExclusive(string path, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        return OpenFile(fullPath, FileMode.Open, FileAccess.Read, allowedRoot, FileShareRead);
    }

    public static Stream OpenWrite(string path, FileMode mode, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        return OpenFile(fullPath, mode, FileAccess.ReadWrite, allowedRoot);
    }

    /// <summary>
    /// Holds every existing directory in <paramref name="path"/> open while a
    /// caller performs a path-based operation. The returned lease must remain
    /// alive until the operation has reached its OS boundary.
    /// </summary>
    public static IDisposable OpenDirectoryLease(string path, string? allowedRoot = null) =>
        AcquireDirectoryLease(path, allowedRoot);

    public static async Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken,
        string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            throw new IOException("A file parent is required.");
        }

        if (allowedRoot is not null && !IsUnderRoot(fullPath, allowedRoot))
        {
            throw new IOException("A secure file path escaped its allowed root.");
        }

        EnsureDirectory(parent, allowedRoot);
        using DirectoryLease parentLease = AcquireDirectoryLease(parent, allowedRoot);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (Stream stream = OpenFile(temporaryPath, FileMode.CreateNew, FileAccess.Write, parentLease))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush();
            }

            MoveReplace(temporaryPath, fullPath, parentLease);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static void MoveCreate(string sourcePath, string destinationPath, string? allowedRoot = null)
    {
        Move(sourcePath, destinationPath, replace: false, allowedRoot);
    }

    public static void MoveReplace(string sourcePath, string destinationPath, string? allowedRoot = null)
    {
        Move(sourcePath, destinationPath, replace: true, allowedRoot);
    }

    public static void MoveDirectoryCreate(string sourcePath, string destinationPath, string? allowedRoot = null)
    {
        string source = NormalizePath(sourcePath);
        string destination = NormalizePath(destinationPath);
        if (allowedRoot is not null && (!IsUnderRoot(source, allowedRoot) || !IsUnderRoot(destination, allowedRoot)))
        {
            throw new IOException("A directory move path escaped its allowed root.");
        }

        string? sourceParent = Path.GetDirectoryName(source);
        string? destinationParent = Path.GetDirectoryName(destination);
        if (sourceParent is null || destinationParent is null)
        {
            throw new IOException("Directory move paths require directory parents.");
        }

        // Bind the source and both parent chains before issuing the rename. The
        // source lease is released immediately before the OS rename because a
        // directory handle can itself prevent a rename on Windows; the parent
        // leases keep the lexical chains stable during that final call.
        using DirectoryLease sourceParentLease = AcquireDirectoryLease(sourceParent, allowedRoot);
        using DirectoryLease destinationParentLease = string.Equals(sourceParent, destinationParent, PathComparison)
            ? sourceParentLease
            : AcquireDirectoryLease(destinationParent, allowedRoot);
        using (DirectoryLease sourceLease = AcquireDirectoryLease(source, allowedRoot))
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            Directory.Move(source, destination);
            return;
        }

        if (!MoveFileExW(source, destination, MoveFileWriteThrough))
        {
            throw new IOException($"Moving a secure directory failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    public static void WriteAtomically(
        string path,
        ReadOnlyMemory<byte> content,
        string? allowedRoot = null) =>
        WriteAtomicallyAsync(path, content, CancellationToken.None, allowedRoot).GetAwaiter().GetResult();

    public static void DeleteFile(string path, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            throw new IOException("A file parent is required.");
        }

        using DirectoryLease lease = AcquireDirectoryLease(parent, allowedRoot);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    public static void DeleteDirectory(string path, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            throw new IOException("A directory parent is required.");
        }

        using DirectoryLease parentLease = AcquireDirectoryLease(parent, allowedRoot);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        using DirectoryLease directoryLease = AcquireDirectoryLease(fullPath, allowedRoot);
        Directory.Delete(fullPath, recursive: true);
    }

    private static void MoveReplace(string sourcePath, string destinationPath, DirectoryLease destinationLease)
    {
        string source = NormalizePath(sourcePath);
        string destination = NormalizePath(destinationPath);
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        if (!MoveFileExW(source, destination, MoveFileReplaceExisting | MoveFileWriteThrough))
        {
            throw new IOException($"Moving a secure file failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    public static bool IsSafeFile(string path, string root)
    {
        try
        {
            string fullPath = NormalizePath(path);
            if (!IsUnderRoot(fullPath, root) || !File.Exists(fullPath))
            {
                return false;
            }

            using Stream stream = OpenRead(fullPath, root);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsSafeDirectory(string path, string? root = null)
    {
        try
        {
            string fullPath = NormalizePath(path);
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            using DirectoryLease lease = AcquireDirectoryLease(fullPath, root);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static LeasedFileStream OpenFile(
        string fullPath,
        FileMode mode,
        FileAccess access,
        string? allowedRoot)
    {
        return OpenFile(fullPath, mode, access, allowedRoot, FileShareRead | FileShareWrite);
    }

    private static LeasedFileStream OpenFile(
        string fullPath,
        FileMode mode,
        FileAccess access,
        string? allowedRoot,
        uint shareMode)
    {
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            throw new IOException("A file parent is required.");
        }

        DirectoryLease lease = AcquireDirectoryLease(parent, allowedRoot);
        try
        {
            return OpenFile(fullPath, mode, access, lease, shareMode);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static LeasedFileStream OpenFile(
        string fullPath,
        FileMode mode,
        FileAccess access,
        DirectoryLease lease)
    {
        return OpenFile(fullPath, mode, access, lease, FileShareRead | FileShareWrite);
    }

    private static LeasedFileStream OpenFile(
        string fullPath,
        FileMode mode,
        FileAccess access,
        DirectoryLease lease,
        uint shareMode)
    {
        if (!OperatingSystem.IsWindows())
        {
            FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan;
            FileShare fallbackShare = shareMode == FileShareRead
                ? FileShare.Read
                : FileShare.Read | FileShare.Write;
            FileStream fallback = new(fullPath, mode, access, fallbackShare, BufferSize, options);
            try
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("A reparse point is not a trusted file object.");
                }

                return new LeasedFileStream(fallback, lease);
            }
            catch
            {
                fallback.Dispose();
                throw;
            }
        }

        uint desiredAccess = access == FileAccess.Read ? GenericRead : GenericRead | GenericWrite;
        uint creation = mode switch
        {
            FileMode.CreateNew => CreateNew,
            FileMode.Create => CreateAlways,
            FileMode.Open => OpenExisting,
            FileMode.OpenOrCreate => OpenAlways,
            FileMode.Truncate => OpenExisting,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        SafeFileHandle handle = CreateFileW(
            fullPath,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creation,
            FileFlagOpenReparsePoint | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Opening a secure file handle failed with Win32 error {error}.");
        }

        try
        {
            EnsureRegularHandle(handle, fullPath);
            if (mode == FileMode.Truncate)
            {
                FileStream truncation = new(handle, FileAccess.Write, BufferSize, isAsync: true);
                try
                {
                    truncation.SetLength(0);
                    truncation.Flush(flushToDisk: true);
                    return new LeasedFileStream(truncation, lease);
                }
                catch
                {
                    truncation.Dispose();
                    throw;
                }
            }

            FileStream stream = new(handle, access, BufferSize, isAsync: true);
            return new LeasedFileStream(stream, lease);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void Move(
        string sourcePath,
        string destinationPath,
        bool replace,
        string? allowedRoot)
    {
        string source = NormalizePath(sourcePath);
        string destination = NormalizePath(destinationPath);
        if (allowedRoot is not null && (!IsUnderRoot(source, allowedRoot) || !IsUnderRoot(destination, allowedRoot)))
        {
            throw new IOException("A move path escaped its allowed root.");
        }

        string? sourceParent = Path.GetDirectoryName(source);
        string? destinationParent = Path.GetDirectoryName(destination);
        if (sourceParent is null || destinationParent is null)
        {
            throw new IOException("Move paths require directory parents.");
        }

        using DirectoryLease sourceLease = AcquireDirectoryLease(sourceParent, allowedRoot);
        using DirectoryLease destinationLease = string.Equals(sourceParent, destinationParent, PathComparison)
            ? sourceLease
            : AcquireDirectoryLease(destinationParent, allowedRoot);
        if (!OperatingSystem.IsWindows())
        {
            if (replace)
            {
                File.Move(source, destination, overwrite: true);
            }
            else
            {
                File.Move(source, destination);
            }

            return;
        }

        uint flags = replace ? MoveFileReplaceExisting : 0;
        flags |= MoveFileWriteThrough;
        if (!MoveFileExW(source, destination, flags))
        {
            throw new IOException($"Moving a secure file failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private static DirectoryLease AcquireDirectoryLease(string path, string? allowedRoot = null)
    {
        string fullPath = NormalizePath(path);
        if (allowedRoot is not null && !IsUnderRoot(fullPath, allowedRoot))
        {
            throw new IOException("A secure directory path escaped its allowed root.");
        }

        if (!OperatingSystem.IsWindows())
        {
            EnsureDirectoryChainNoReparse(fullPath, allowedRoot);
            return new DirectoryLease([]);
        }

        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            throw new IOException("A secure directory path has no root.");
        }

        List<SafeFileHandle> handles = [];
        try
        {
            string current = pathRoot;
            string relative = fullPath[pathRoot.Length..];
            foreach (string segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                SafeFileHandle handle = CreateFileW(
                    current,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException($"Opening a secure directory handle failed with Win32 error {error}.");
                }

                try
                {
                    EnsureDirectoryHandle(handle, current);
                    handles.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            return new DirectoryLease(handles);
        }
        catch
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static void EnsureDirectoryChainNoReparse(string path, string? allowedRoot)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("A secure directory path has no root.");
        }

        string current = root;
        foreach (string segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A secure directory path contains a reparse point or missing component.");
            }
        }

        if (allowedRoot is not null && !IsUnderRoot(path, allowedRoot))
        {
            throw new IOException("A secure directory path escaped its allowed root.");
        }
    }

    private static void EnsureDirectoryHandle(SafeFileHandle handle, string expectedPath)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info) ||
            (info.FileAttributes & ReparsePointAttribute) != 0)
        {
            throw new IOException("A secure directory path contains a reparse point.");
        }

        string actualPath = NormalizeHandlePath(GetFinalPath(handle));
        if (!string.Equals(actualPath, NormalizePath(expectedPath), PathComparison))
        {
            throw new IOException("A secure directory path resolved outside its lexical identity.");
        }
    }

    private static void EnsureRegularHandle(SafeFileHandle handle, string expectedPath)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info) ||
            (info.FileAttributes & ReparsePointAttribute) != 0)
        {
            throw new IOException("A secure file path contains a reparse point.");
        }

        string actualPath = NormalizeHandlePath(GetFinalPath(handle));
        if (!string.Equals(actualPath, NormalizePath(expectedPath), PathComparison))
        {
            throw new IOException("A secure file path resolved outside its lexical identity.");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        StringBuilder buffer = new(512);
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new IOException($"Resolving a secure file handle failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            if (length < buffer.Capacity)
            {
                return buffer.ToString();
            }

            buffer.Capacity = checked(buffer.Capacity * 2);
        }
    }

    private static string NormalizeHandlePath(string path)
    {
        string normalized = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;
        return NormalizePath(normalized);
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string fullPath = NormalizePath(path);
        string fullRoot = NormalizePath(root);
        return string.Equals(fullPath, fullRoot, PathComparison) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static void TryDelete(string path)
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

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class DirectoryLease(IReadOnlyList<SafeFileHandle> handles) : IDisposable
    {
        private readonly IReadOnlyList<SafeFileHandle> handles = handles;

        public void Dispose()
        {
            foreach (SafeFileHandle handle in handles.Reverse())
            {
                handle.Dispose();
            }
        }
    }

    private sealed class LeasedFileStream(Stream inner, IDisposable lease) : Stream
    {
        private readonly Stream inner = inner;
        private readonly IDisposable lease = lease;
        private bool disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                if (disposing)
                {
                    inner.Dispose();
                    lease.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                await inner.DisposeAsync();
                lease.Dispose();
            }

            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

}

#pragma warning restore CA1838
