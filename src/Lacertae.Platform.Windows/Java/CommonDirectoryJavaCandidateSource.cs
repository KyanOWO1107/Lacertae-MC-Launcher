using Lacertae.Application.Java;
using Lacertae.Application.Storage;
using Lacertae.Domain.Java;

namespace Lacertae.Platform.Windows.Java;

public sealed class CommonDirectoryJavaCandidateSource : IJavaCandidateSource
{
    private readonly IReadOnlyList<string> roots;
    private readonly IFileSystem fileSystem;
    private readonly int maximumDepth;

    public CommonDirectoryJavaCandidateSource(
        IReadOnlyList<string> roots,
        IFileSystem fileSystem,
        int maximumDepth = 3)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDepth);
        this.roots = roots;
        this.fileSystem = fileSystem;
        this.maximumDepth = maximumDepth;
    }

    public async IAsyncEnumerable<JavaCandidate> FindCandidatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = fileSystem.GetFullPath(root);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            foreach (string executable in EnumerateExecutables(normalizedRoot, 0, cancellationToken))
            {
                if (emitted.Add(executable))
                {
                    yield return new JavaCandidate(executable, JavaSource.CommonDirectory, false);
                }
            }

            await Task.Yield();
        }
    }

    private IEnumerable<string> EnumerateExecutables(string directory, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string executableName in new[] { "javaw.exe", "java.exe" })
        {
            string path = Path.Combine(directory, "bin", executableName);
            if (fileSystem.FileExists(path))
            {
                yield return fileSystem.GetFullPath(path);
            }
        }

        if (depth >= maximumDepth || !fileSystem.DirectoryExists(directory))
        {
            yield break;
        }

        IEnumerable<string> childDirectories;
        try
        {
            childDirectories = Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (string child in childDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string executable in EnumerateExecutables(child, depth + 1, cancellationToken))
            {
                yield return executable;
            }
        }
    }
}
