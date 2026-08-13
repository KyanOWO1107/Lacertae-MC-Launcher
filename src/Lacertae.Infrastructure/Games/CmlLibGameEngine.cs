using System.Globalization;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using Lacertae.Application.Games;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Infrastructure.Games;

public sealed class CmlLibGameEngine(string gameRoot) : IGameEngine
{
    private readonly string gameRoot = Path.GetFullPath(
        string.IsNullOrWhiteSpace(gameRoot)
            ? throw new ArgumentException("Game root cannot be blank.", nameof(gameRoot))
            : gameRoot);

    public async Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
        CancellationToken cancellationToken)
    {
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
                    new JavaRequirement(javaVersion.JavaVersion.Component, majorVersion)));
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
}
