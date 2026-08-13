using System.Runtime.Versioning;
using Lacertae.Platform.Windows.Java;

namespace Lacertae.Platform.Windows.Tests.Java;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryJavaReaderTests
{
    [Fact]
    public void ReadEntriesDoesNotThrowWhenDocumentedKeysAreUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsRegistryJavaReader reader = new();
        _ = reader.ReadEntries().ToList();
    }
}
