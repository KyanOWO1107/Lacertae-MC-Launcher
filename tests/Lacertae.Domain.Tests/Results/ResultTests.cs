using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Domain.Tests.Results;

public sealed class ResultTests
{
    [Fact]
    public void SuccessContainsValueAndNoProblem()
    {
        Result<int> result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void FailureContainsProblemAndValueAccessFailsLoudly()
    {
        Problem problem = new(
            "JAVA_INCOMPATIBLE",
            ProblemStage.JavaResolution,
            "problem.java.incompatible",
            false,
            "corr-1",
            ["action.java.choose"]);

        Result<int> result = Result<int>.Failure(problem);

        Assert.False(result.IsSuccess);
        Assert.Same(problem, result.Problem);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _ = result.Value);
        Assert.Contains("JAVA_INCOMPATIBLE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemRejectsBlankCode()
    {
        Assert.Throws<ArgumentException>(() => new Problem(
            " ", ProblemStage.Unknown, "problem.unknown", false, "corr-1", []));
    }
}
