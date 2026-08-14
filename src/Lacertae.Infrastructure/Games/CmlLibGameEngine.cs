using System.Globalization;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using Lacertae.Application.Games;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Infrastructure.Games;

public sealed class CmlLibGameEngine : IGameEngine
{
    private readonly CmlLibProcessFactory processFactory;

    public CmlLibGameEngine(CmlLibProcessFactory? processFactory = null)
    {
        this.processFactory = processFactory ?? new CmlLibProcessFactory();
    }

    public Task<Result<GameProcessSpec>> BuildProcessSpecAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken) =>
        processFactory.BuildProcessSpecAsync(plan, cancellationToken);

    public async Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
        string gameRootPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        string gameRoot = Path.GetFullPath(gameRootPath);
        try
        {
            MinecraftPath path = new(gameRoot);
            LocalJsonVersionLoader loader = new(path);
            VersionMetadataCollection collection = await loader.GetVersionMetadatasAsync(cancellationToken);
            List<GameVersionDescriptor> versions = [];

            foreach (IVersionMetadata metadata in collection)
            {
                IVersion version = await collection.GetVersionAsync(metadata.Name, cancellationToken);
                IVersion javaVersion = version;
                while (javaVersion.JavaVersion is null && javaVersion.ParentVersion is not null)
                {
                    javaVersion = javaVersion.ParentVersion;
                }

                if (javaVersion.JavaVersion is null ||
                    !int.TryParse(javaVersion.JavaVersion.MajorVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out int majorVersion) ||
                    majorVersion < 1)
                {
                    throw new VersionParseException($"Java version is missing for '{metadata.Name}'.");
                }

                versions.Add(new GameVersionDescriptor(
                    gameRoot,
                    metadata.Name,
                    metadata.Name,
                    version.Type ?? "unknown",
                    version.InheritsFrom,
                    new JavaRequirement(javaVersion.JavaVersion.Component, majorVersion),
                    HasKnownLoader(version)));
            }

            return Result<IReadOnlyList<GameVersionDescriptor>>.Success(versions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or VersionParseException or VersionDependencyException)
        {
            return Result<IReadOnlyList<GameVersionDescriptor>>.Failure(new Problem(
                "VERSION_PARSE_FAILED",
                ProblemStage.VersionResolution,
                "problem.version.parse_failed",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.version.inspect_again"],
                new Dictionary<string, string> { ["gameRoot"] = Path.GetFileName(gameRoot) }));
        }
    }

    private static bool HasKnownLoader(IVersion version)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        for (IVersion? current = version; current is not null; current = current.ParentVersion)
        {
            if (!visited.Add(current.Id))
            {
                break;
            }

            if (current.Libraries is not null && current.Libraries.Any(static library => IsKnownLoaderCoordinate(library.Name)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownLoaderCoordinate(string? coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
        {
            return false;
        }

        string[] parts = coordinate.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        return (string.Equals(parts[0], "net.fabricmc", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "fabric-loader", StringComparison.OrdinalIgnoreCase)) ||
               (string.Equals(parts[0], "net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "forge", StringComparison.OrdinalIgnoreCase)) ||
               (string.Equals(parts[0], "net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "neoforge", StringComparison.OrdinalIgnoreCase)) ||
               (string.Equals(parts[0], "org.quiltmc", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "quilt-loader", StringComparison.OrdinalIgnoreCase));
    }
}
