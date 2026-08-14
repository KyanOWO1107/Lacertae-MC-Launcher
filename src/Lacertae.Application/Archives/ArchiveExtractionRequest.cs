namespace Lacertae.Application.Archives;

public sealed record ArchiveExtractionRequest(
    string ArchivePath,
    string DestinationDirectory,
    int MaximumEntries,
    long MaximumExpandedBytes,
    int MaximumExpansionRatio,
    bool AllowLinks);
