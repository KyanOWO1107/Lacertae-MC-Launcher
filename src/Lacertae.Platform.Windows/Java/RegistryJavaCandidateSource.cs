using System.Runtime.Versioning;
using Lacertae.Application.Java;
using Lacertae.Application.Storage;
using Lacertae.Domain.Java;
using Microsoft.Win32;

namespace Lacertae.Platform.Windows.Java;

public sealed record JavaRegistryEntry(string JavaHome, string? ExecutablePathOverride = null);

public interface IJavaRegistryReader
{
    IEnumerable<JavaRegistryEntry> ReadEntries();
}

public sealed class RegistryJavaCandidateSource(
    IJavaRegistryReader registryReader,
    IFileSystem fileSystem) : IJavaCandidateSource
{
    public async IAsyncEnumerable<JavaCandidate> FindCandidatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (JavaRegistryEntry entry in registryReader.ReadEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.JavaHome))
            {
                continue;
            }

            string? candidate = FindExecutable(entry);
            if (candidate is not null && emitted.Add(candidate))
            {
                yield return new JavaCandidate(candidate, JavaSource.Registry, false);
            }

            await Task.Yield();
        }
    }

    private string? FindExecutable(JavaRegistryEntry entry)
    {
        string home;
        try
        {
            home = fileSystem.GetFullPath(Environment.ExpandEnvironmentVariables(entry.JavaHome));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        string[] candidates = entry.ExecutablePathOverride is null
            ? [Path.Combine(home, "bin", "javaw.exe"), Path.Combine(home, "bin", "java.exe")]
            : [Environment.ExpandEnvironmentVariables(entry.ExecutablePathOverride)];
        foreach (string rawCandidate in candidates)
        {
            string candidate;
            try
            {
                candidate = fileSystem.GetFullPath(rawCandidate);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            if (!IsUnderHome(candidate, home) || !fileSystem.FileExists(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsUnderHome(string path, string home)
    {
        string normalizedHome = Path.TrimEndingDirectorySeparator(home) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedHome, StringComparison.OrdinalIgnoreCase);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryJavaReader : IJavaRegistryReader
{
    private static readonly string[] SubKeyNames =
    [
        "SOFTWARE\\JavaSoft\\Java Runtime Environment",
        "SOFTWARE\\JavaSoft\\JDK",
        "SOFTWARE\\Eclipse Adoptium",
    ];

    public IEnumerable<JavaRegistryEntry> ReadEntries()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (string subKeyName in SubKeyNames)
                {
                    foreach (JavaRegistryEntry entry in ReadKey(hive, view, subKeyName))
                    {
                        yield return entry;
                    }
                }
            }
        }
    }

    private static IEnumerable<JavaRegistryEntry> ReadKey(RegistryHive hive, RegistryView view, string subKeyName)
    {
        using RegistryKey? baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? key = baseKey.OpenSubKey(subKeyName);
        if (key is null)
        {
            yield break;
        }

        string? currentVersion = key.GetValue("CurrentVersion") as string;
        if (!string.IsNullOrWhiteSpace(currentVersion) && key.OpenSubKey(currentVersion) is RegistryKey currentKey)
        {
            using (currentKey)
            {
                if (currentKey.GetValue("JavaHome") is string javaHome)
                {
                    yield return new JavaRegistryEntry(javaHome);
                }
            }
        }

        foreach (string versionName in key.GetSubKeyNames())
        {
            using RegistryKey? versionKey = key.OpenSubKey(versionName);
            if (versionKey?.GetValue("JavaHome") is string javaHome)
            {
                yield return new JavaRegistryEntry(javaHome);
            }
        }
    }
}
