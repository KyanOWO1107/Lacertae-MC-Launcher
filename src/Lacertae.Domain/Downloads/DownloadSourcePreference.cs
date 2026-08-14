namespace Lacertae.Domain.Downloads;

public sealed record DownloadSourcePreference(DownloadSourceId? PinnedSourceId = null)
{
    public static DownloadSourcePreference Automatic { get; } = new();

    public bool IsAutomatic => PinnedSourceId is null;

    public static DownloadSourcePreference Pinned(DownloadSourceId sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        return new DownloadSourcePreference(sourceId);
    }
}
