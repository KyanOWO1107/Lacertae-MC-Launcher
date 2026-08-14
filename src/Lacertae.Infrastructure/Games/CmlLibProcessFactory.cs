using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using Lacertae.Application.Games;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Games;

public sealed class CmlLibProcessFactory
{
    private readonly TimeProvider timeProvider;

    public CmlLibProcessFactory(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<GameProcessSpec>> BuildProcessSpecAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            string root = NormalizeDirectory(plan.GameRootPath);
            string gameDirectory = NormalizeDirectory(plan.GameDirectory);
            if (!IsUnderRoot(gameDirectory, root))
            {
                return Result<GameProcessSpec>.Failure(Problem("GAME_PROCESS_PATH_OUTSIDE_ROOT"));
            }

            MinecraftPath path = new(gameDirectory, root)
            {
                Library = Path.Combine(root, "libraries"),
                Versions = Path.Combine(root, "versions"),
                Resource = Path.Combine(root, "resources"),
                Assets = Path.Combine(root, "assets"),
                Runtime = Path.Combine(root, "runtime"),
            };
            AssertSharedPath(path.Library, root);
            AssertSharedPath(path.Versions, root);
            AssertSharedPath(path.Resource, root);
            AssertSharedPath(path.Assets, root);
            AssertSharedPath(path.Runtime, root);

            LocalJsonVersionLoader loader = new(path);
            VersionMetadataCollection metadata = await loader.GetVersionMetadatasAsync(cancellationToken);
            IVersion version = await metadata.GetVersionAsync(plan.VersionId, cancellationToken);

            MLaunchOption launchOption = new()
            {
                Path = path,
                StartVersion = version,
                JavaPath = plan.JavaExecutablePath,
                Session = CmlLibSessionMapper.Map(plan.Session),
                MinimumRamMb = plan.MinimumMemoryMb,
                MaximumRamMb = plan.MaximumMemoryMb,
                JvmArgumentOverrides = plan.FlattenedJvmArguments.Select(static argument => new MArgument(argument)).ToArray(),
                ExtraJvmArguments = plan.FlattenedJvmArguments.Select(static argument => new MArgument(argument)).ToArray(),
                ExtraGameArguments = plan.GameArguments.Select(static argument => new MArgument(argument)).ToArray(),
            };

            using Process process = new MinecraftLauncher(path).BuildProcess(version, launchOption);
            ProcessStartInfo startInfo = process.StartInfo;
            if (string.IsNullOrWhiteSpace(startInfo.FileName))
            {
                return Result<GameProcessSpec>.Failure(Problem("GAME_PROCESS_EXECUTABLE_MISSING"));
            }

            Result<string[]> parsedArguments = WindowsCommandLineParser.Parse(startInfo.Arguments ?? string.Empty);
            if (!parsedArguments.IsSuccess)
            {
                return Result<GameProcessSpec>.Failure(parsedArguments.Problem!);
            }

            SensitiveString[] arguments = parsedArguments.Value
                .Select(static argument => new SensitiveString(argument))
                .ToArray();
            return Result<GameProcessSpec>.Success(new GameProcessSpec(
                Path.GetFullPath(startInfo.FileName),
                arguments,
                gameDirectory,
                new Dictionary<string, SensitiveString>(StringComparer.Ordinal),
                plan.CorrelationId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or VersionParseException or VersionDependencyException or ArgumentException or InvalidOperationException)
        {
            return Result<GameProcessSpec>.Failure(Problem(
                "GAME_PROCESS_BUILD_FAILED",
                exception is ArgumentException ? "problem.game.process_invalid" : "problem.game.process_build_failed"));
        }
    }

    public Task<Result<GameProcessSpec>> CreateAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken) => BuildProcessSpecAsync(plan, cancellationToken);

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void AssertSharedPath(string path, string root)
    {
        if (!IsUnderRoot(path, root))
        {
            throw new ArgumentException("CmlLib shared path escapes the game root.", nameof(path));
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedPath = NormalizeDirectory(path);
        string normalizedRoot = NormalizeDirectory(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private Problem Problem(
        string code,
        string messageKey = "problem.game.process_build_failed") => new(
        code,
        ProblemStage.Process,
        messageKey,
        false,
        $"{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
        ["action.launch.review_settings"]);
}

internal static class WindowsCommandLineParser
{
    public static Result<string[]> Parse(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        List<string> values = [];
        StringBuilder current = new();
        bool inQuotes = false;
        bool tokenStarted = false;

        for (int index = 0; index < commandLine.Length;)
        {
            char character = commandLine[index];
            if (character is ' ' or '\t' && !inQuotes)
            {
                if (tokenStarted)
                {
                    values.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                index++;
                continue;
            }

            if (character == '\\')
            {
                int slashStart = index;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    index++;
                }

                int slashCount = index - slashStart;
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                    {
                        tokenStarted = true;
                        inQuotes = !inQuotes;
                        index++;
                    }
                    else
                    {
                        current.Append('"');
                        tokenStarted = true;
                        index++;
                    }
                }
                else
                {
                    current.Append('\\', slashCount);
                    tokenStarted = true;
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                index++;
                continue;
            }

            current.Append(character);
            tokenStarted = true;
            index++;
        }

        if (inQuotes)
        {
            return Result<string[]>.Failure(new Problem(
                "GAME_PROCESS_ARGUMENTS_INVALID",
                ProblemStage.Process,
                "problem.game.process_arguments_invalid",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.launch.review_settings"]));
        }

        if (tokenStarted)
        {
            values.Add(current.ToString());
        }

        return Result<string[]>.Success(values.ToArray());
    }
}
