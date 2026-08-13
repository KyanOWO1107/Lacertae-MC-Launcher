using Lacertae.Domain.Java;
using Lacertae.Domain.Problems;

namespace Lacertae.Application.Java;

public sealed record JavaCandidate(
    string ExecutablePath,
    JavaSource Source,
    bool IsManaged);

public sealed record JavaDiscoveryResult(
    IReadOnlyList<JavaInstallation> Installations,
    IReadOnlyList<Problem> Diagnostics);

public interface IJavaCandidateSource
{
    IAsyncEnumerable<JavaCandidate> FindCandidatesAsync(CancellationToken cancellationToken);
}

public interface IPathComparer
{
    string Normalize(string path);
    bool Equals(string left, string right);
}
