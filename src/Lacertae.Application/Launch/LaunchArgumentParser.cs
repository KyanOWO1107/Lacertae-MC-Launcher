using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Launch;

public static class LaunchArgumentParser
{
    public static Result<IReadOnlyList<string>> ParseLines(
        IReadOnlyList<string>? values,
        string problemCode = "LAUNCH_PLAN_INVALID_ARGUMENTS")
    {
        if (values is null)
        {
            return Failure(problemCode);
        }

        List<string> parsed = [];
        foreach (string? value in values)
        {
            if (value is null || value.Contains('\0'))
            {
                return Failure(problemCode);
            }

            string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            foreach (string line in lines)
            {
                parsed.Add(line.Trim());
            }
        }

        int first = 0;
        while (first < parsed.Count && parsed[first].Length == 0)
        {
            first++;
        }

        int last = parsed.Count - 1;
        while (last >= first && parsed[last].Length == 0)
        {
            last--;
        }

        if (first <= last && parsed[first..(last + 1)].Any(static value => value.Length == 0))
        {
            return Failure(problemCode);
        }

        return Result<IReadOnlyList<string>>.Success(
            first > last ? [] : parsed[first..(last + 1)].ToArray());
    }

    private static Result<IReadOnlyList<string>> Failure(string code) =>
        Result<IReadOnlyList<string>>.Failure(new Problem(
            code,
            ProblemStage.LaunchPlanning,
            "problem.launch.arguments_invalid",
            false,
            Guid.NewGuid().ToString("N"),
            ["action.launch.review_settings"]));
}
