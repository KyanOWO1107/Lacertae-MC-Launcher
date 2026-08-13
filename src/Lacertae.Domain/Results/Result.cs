using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;

namespace Lacertae.Domain.Results;

public sealed class Result<T>
{
    private readonly T? value;

    private Result(bool isSuccess, T? value, Problem? problem) =>
        (IsSuccess, this.value, Problem) = (isSuccess, value, problem);

    public bool IsSuccess { get; }
    public Problem? Problem { get; }
    public T Value => IsSuccess
        ? value!
        : throw new InvalidOperationException($"Cannot access value for failed result '{Problem?.Code}'.");

#pragma warning disable CA1000
    public static Result<T> Success(T value) => new(true, value, null);

    public static Result<T> Failure(Problem problem) =>
        new(false, default, problem ?? throw new ArgumentNullException(nameof(problem)));
#pragma warning restore CA1000
}

public static class Result
{
    public static Result<Unit> Success() => Result<Unit>.Success(Unit.Value);

    public static Result<Unit> Failure(Problem problem) => Result<Unit>.Failure(problem);
}
