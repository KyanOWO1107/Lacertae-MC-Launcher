using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lacertae.Application.SystemInfo;

namespace Lacertae.Platform.Windows.SystemInfo;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsMemoryInfo : IMemoryInfo
{
    public MemorySnapshot GetSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowsMemoryInfo requires Windows.");
        }

        MemoryStatusEx status = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!NativeMethods.TryGetMemoryStatus(ref status))
        {
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        return new MemorySnapshot(status.TotalPhysicalBytes, status.AvailablePhysicalBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        public static bool TryGetMemoryStatus(ref MemoryStatusEx status) => GlobalMemoryStatusEx(ref status);
    }
}
