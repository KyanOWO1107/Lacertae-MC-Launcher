namespace Lacertae.Application.Install;

public interface IInstallEnvironment
{
    bool DirectoryExists(string path);

    bool IsDirectoryWritable(string path);

    long GetAvailableFreeBytes(string path);
}

public sealed class SystemInstallEnvironment : IInstallEnvironment
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsDirectoryWritable(string path)
    {
        string probe = Path.Combine(path, ".lacertae-install-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Flush(true);
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
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
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

    public long GetAvailableFreeBytes(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException("The install path has no volume root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
