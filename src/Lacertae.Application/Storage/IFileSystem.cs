namespace Lacertae.Application.Storage;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    bool IsDirectoryWritable(string path);
    string GetFullPath(string path);
}
