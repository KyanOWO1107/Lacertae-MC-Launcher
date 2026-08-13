using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;

namespace Lacertae.Application.Startup;

public sealed record StartupState(
    DataRoot DataRoot,
    LauncherSettings Settings,
    IReadOnlyList<GameRoot> GameRoots);
