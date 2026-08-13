using Lacertae.Application.Storage;

namespace Lacertae.Testing.Storage;

public sealed class FakeFileSystem : IFileSystem
{
    private readonly HashSet<string> existingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unwritableDirectories = new(StringComparer.OrdinalIgnoreCase);

    public void SetFileExists(string path, bool exists)
    {
        if (exists)
        {
            existingFiles.Add(path);
        }
        else
        {
            existingFiles.Remove(path);
        }
    }

    public void SetDirectoryWritable(string path, bool writable)
    {
        if (writable)
        {
            unwritableDirectories.Remove(path);
        }
        else
        {
            unwritableDirectories.Add(path);
        }
    }

    public bool FileExists(string path) => existingFiles.Contains(path);
    public bool DirectoryExists(string path) => true;
    public void CreateDirectory(string path)
    {
    }

    public bool IsDirectoryWritable(string path) => !unwritableDirectories.Contains(path);
    public string GetFullPath(string path) => path;
}
