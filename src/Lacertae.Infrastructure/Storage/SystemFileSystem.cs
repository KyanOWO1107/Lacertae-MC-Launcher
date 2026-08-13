using Lacertae.Application.Storage;

namespace Lacertae.Infrastructure.Storage;

public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool IsDirectoryWritable(string path)
    {
        string probe = Path.Combine(path, ".lacertae-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Flush(true);
            }

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
        finally
        {
            File.Delete(probe);
        }
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
