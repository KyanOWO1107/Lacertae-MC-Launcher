namespace Lacertae.Domain.Java;

public sealed record JavaSelection(
    JavaInstallation Installation,
    JavaSelectionMode Mode);
