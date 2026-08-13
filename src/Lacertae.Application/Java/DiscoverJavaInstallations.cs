using Lacertae.Application.Storage;
using Lacertae.Domain.Java;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Java;

public sealed class DiscoverJavaInstallations(
    IReadOnlyList<IJavaCandidateSource> sources,
    IJavaProbe probe,
    IFileSystem fileSystem,
    IPathComparer pathComparer)
{
    private const int MaxProbeConcurrency = 4;

    public async Task<Result<JavaDiscoveryResult>> ExecuteAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(pathComparer);

        List<JavaCandidate> candidates = [];
        List<Problem> diagnostics = [];
        int successfulSources = 0;

        foreach (IJavaCandidateSource source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            try
            {
                await foreach (JavaCandidate candidate in source.FindCandidatesAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(candidate.ExecutablePath))
                    {
                        diagnostics.Add(DiscoveryProblem("JAVA_CANDIDATE_INVALID", source));
                        continue;
                    }

                    string normalizedPath;
                    try
                    {
                        normalizedPath = pathComparer.Normalize(fileSystem.GetFullPath(candidate.ExecutablePath));
                    }
                    catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
                    {
                        diagnostics.Add(DiscoveryProblem("JAVA_CANDIDATE_INVALID", source));
                        continue;
                    }

                    if (!fileSystem.FileExists(normalizedPath))
                    {
                        diagnostics.Add(DiscoveryProblem("JAVA_CANDIDATE_NOT_FOUND", source));
                        continue;
                    }

                    JavaCandidate normalizedCandidate = candidate with { ExecutablePath = normalizedPath };
                    int existingIndex = candidates.FindIndex(existing =>
                        pathComparer.Equals(existing.ExecutablePath, normalizedPath));
                    if (existingIndex < 0)
                    {
                        candidates.Add(normalizedCandidate);
                    }
                    else if (!candidates[existingIndex].IsManaged && normalizedCandidate.IsManaged)
                    {
                        candidates[existingIndex] = normalizedCandidate;
                    }
                }

                successfulSources++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                diagnostics.Add(DiscoveryProblem("JAVA_SOURCE_ENUMERATION_FAILED", source));
            }
        }

        if (successfulSources == 0 && sources.Count > 0)
        {
            return Result<JavaDiscoveryResult>.Failure(new Problem(
                "JAVA_DISCOVERY_FAILED",
                ProblemStage.JavaResolution,
                "problem.java.discovery_failed",
                true,
                Guid.NewGuid().ToString("N"),
                ["action.java.retry_discovery"]));
        }

        using SemaphoreSlim semaphore = new(MaxProbeConcurrency, MaxProbeConcurrency);
        List<Task> probeTasks = [];
        List<JavaInstallation> installations = [];
        object resultGate = new();
        foreach (JavaCandidate candidate in candidates)
        {
            probeTasks.Add(ProbeCandidateAsync(candidate));
        }

        await Task.WhenAll(probeTasks);
        installations.Sort(static (left, right) => CompareInstallations(left, right));
        return Result<JavaDiscoveryResult>.Success(new JavaDiscoveryResult(installations, diagnostics));

        async Task ProbeCandidateAsync(JavaCandidate candidate)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                Result<JavaInstallation> result;
                try
                {
                    result = await probe.ProbeAsync(
                        candidate.ExecutablePath,
                        candidate.Source,
                        candidate.IsManaged,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    result = Result<JavaInstallation>.Failure(DiscoveryProblem("JAVA_PROBE_FAILED", candidate));
                }

                lock (resultGate)
                {
                    if (result.IsSuccess)
                    {
                        installations.Add(result.Value);
                    }
                    else
                    {
                        diagnostics.Add(result.Problem!);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private static int CompareInstallations(JavaInstallation left, JavaInstallation right)
    {
        int managedComparison = right.IsManaged.CompareTo(left.IsManaged);
        if (managedComparison != 0)
        {
            return managedComparison;
        }

        int majorComparison = right.MajorVersion.CompareTo(left.MajorVersion);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        int fullVersionComparison = CompareVersionStrings(left.FullVersion, right.FullVersion);
        return fullVersionComparison != 0
            ? fullVersionComparison
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int CompareVersionStrings(string left, string right)
    {
        int[] leftParts = ParseVersionParts(left);
        int[] rightParts = ParseVersionParts(right);
        int count = Math.Max(leftParts.Length, rightParts.Length);
        for (int index = 0; index < count; index++)
        {
            int leftPart = index < leftParts.Length ? leftParts[index] : 0;
            int rightPart = index < rightParts.Length ? rightParts[index] : 0;
            int comparison = rightPart.CompareTo(leftPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int[] ParseVersionParts(string version) =>
        version.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => int.TryParse(part, out int value) ? value : 0)
            .ToArray();

    private static Problem DiscoveryProblem(string code, object source) => new(
        code,
        ProblemStage.JavaResolution,
        "problem.java.discovery_failed",
        code is "JAVA_SOURCE_ENUMERATION_FAILED" or "JAVA_PROBE_FAILED",
        Guid.NewGuid().ToString("N"),
        ["action.java.review_discovery"],
        new Dictionary<string, string> { ["source"] = source.GetType().Name });
}
