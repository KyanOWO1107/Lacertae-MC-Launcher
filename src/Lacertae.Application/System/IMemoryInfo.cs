namespace Lacertae.Application.SystemInfo;

public interface IMemoryInfo
{
    MemorySnapshot GetSnapshot();
}

public sealed record MemorySnapshot(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes)
{
    private const ulong BytesPerMegabyte = 1024UL * 1024UL;

    public long TotalPhysicalMb => ToMegabytes(TotalPhysicalBytes);

    public long AvailablePhysicalMb => ToMegabytes(AvailablePhysicalBytes);

    private static long ToMegabytes(ulong bytes) => checked((long)(bytes / BytesPerMegabyte));
}
