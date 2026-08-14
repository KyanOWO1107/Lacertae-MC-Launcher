using System.Collections.Concurrent;

namespace Lacertae.Application.Install;

internal static class InstallRootLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim Get(string normalizedRoot) =>
        Locks.GetOrAdd(normalizedRoot, static _ => new SemaphoreSlim(1, 1));

    public static bool IsBusy(string normalizedRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalizedRoot));
        return Locks.TryGetValue(root, out SemaphoreSlim? semaphore) && semaphore.CurrentCount == 0;
    }
}
