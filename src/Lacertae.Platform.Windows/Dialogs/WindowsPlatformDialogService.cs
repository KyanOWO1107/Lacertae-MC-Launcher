using System.Diagnostics;
using Lacertae.Application.Platform;

namespace Lacertae.Platform.Windows.Dialogs;

public sealed class WindowsPlatformDialogService : IPlatformDialogService
{
    public void OpenDirectory(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) || !Path.IsPathRooted(normalizedPath))
        {
            throw new ArgumentException("The directory path must be an absolute path.", nameof(normalizedPath));
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = normalizedPath,
            UseShellExecute = true,
        });
    }
}
