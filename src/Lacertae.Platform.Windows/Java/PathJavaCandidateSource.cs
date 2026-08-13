using Lacertae.Application.Java;
using Lacertae.Application.Storage;
using Lacertae.Domain.Java;

namespace Lacertae.Platform.Windows.Java;

public sealed class PathJavaCandidateSource : IJavaCandidateSource
{
    private readonly string pathValue;
    private readonly IFileSystem fileSystem;

    public PathJavaCandidateSource(string pathValue, IFileSystem fileSystem)
    {
        this.pathValue = pathValue ?? throw new ArgumentNullException(nameof(pathValue));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async IAsyncEnumerable<JavaCandidate> FindCandidatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawEntry in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entry = rawEntry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            string normalizedDirectory;
            try
            {
                normalizedDirectory = fileSystem.GetFullPath(entry);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            foreach (string executableName in new[] { "javaw.exe", "java.exe" })
            {
                string executablePath = fileSystem.GetFullPath(Path.Combine(normalizedDirectory, executableName));
                if (fileSystem.FileExists(executablePath) && emitted.Add(executablePath))
                {
                    yield return new JavaCandidate(executablePath, JavaSource.Path, false);
                }
            }

            await Task.Yield();
        }
    }
}
