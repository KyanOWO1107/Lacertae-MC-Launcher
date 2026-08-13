namespace Lacertae.Domain.Versions;

public sealed record GameVersionDescriptor(
    string GameRootId,
    string FolderName,
    string DisplayName,
    string VersionType,
    string? InheritsFrom,
    JavaRequirement Java);
