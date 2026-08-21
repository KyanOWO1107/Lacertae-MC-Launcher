using System.Diagnostics;
using Lacertae.Application.Platform;
using Lacertae.Application.Storage;

namespace Lacertae.Platform.Windows.Dialogs;

public sealed class WindowsPlatformDialogService : IPlatformDialogService
{
    public void OpenDirectory(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) ||
            !Path.IsPathFullyQualified(normalizedPath) ||
            !SecureFileSystem.IsSafeDirectory(normalizedPath))
        {
            throw new ArgumentException("The directory path must be an absolute path.", nameof(normalizedPath));
        }

        // Keep the validated directory object and its parent chain bound until
        // Explorer has consumed the path. This closes the check-to-open window
        // for a local reparse-point substitution.
        using IDisposable directoryLease = SecureFileSystem.OpenDirectoryLease(normalizedPath);

        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(normalizedPath);
        _ = Process.Start(startInfo);
    }
}
