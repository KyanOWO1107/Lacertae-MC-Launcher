namespace Lacertae.Application.Platform;

/// <summary>
/// Opens an application-approved directory using the host platform.
/// The application is responsible for normalizing and authorizing the path
/// before it reaches this boundary.
/// </summary>
public interface IPlatformDialogService
{
    void OpenDirectory(string normalizedPath);
}
