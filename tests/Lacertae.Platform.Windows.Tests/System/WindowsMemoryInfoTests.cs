using System.Runtime.Versioning;
using Lacertae.Application.SystemInfo;
using Lacertae.Platform.Windows.SystemInfo;

namespace Lacertae.Platform.Windows.Tests.System;

[SupportedOSPlatform("windows")]
public sealed class WindowsMemoryInfoTests
{
    [Fact]
    public void GetSnapshotReturnsUsablePhysicalMemoryValues()
    {
        MemorySnapshot snapshot = new WindowsMemoryInfo().GetSnapshot();

        Assert.True(snapshot.TotalPhysicalBytes > 0);
        Assert.True(snapshot.AvailablePhysicalBytes > 0);
        Assert.True(snapshot.AvailablePhysicalBytes <= snapshot.TotalPhysicalBytes);
    }

    [Fact]
    public void SnapshotConvertsBytesToWholeMegabytes()
    {
        MemorySnapshot snapshot = new(
            TotalPhysicalBytes: 3UL * 1024 * 1024 + 999,
            AvailablePhysicalBytes: 2UL * 1024 * 1024 + 999);

        Assert.Equal(3, snapshot.TotalPhysicalMb);
        Assert.Equal(2, snapshot.AvailablePhysicalMb);
    }
}
