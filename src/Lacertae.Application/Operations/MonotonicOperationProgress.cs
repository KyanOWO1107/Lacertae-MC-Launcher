using Lacertae.Domain.Operations;

namespace Lacertae.Application.Operations;

/// <summary>
/// Normalizes progress from use cases that report per-artifact values into one
/// operation stream. Totals and completed values never move backwards.
/// </summary>
internal sealed class MonotonicOperationProgress(IProgress<OperationProgress> sink) : IProgress<OperationProgress>
{
    private readonly object gate = new();
    private OperationProgress? last;

    public void Report(OperationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (gate)
        {
            string stage = string.IsNullOrWhiteSpace(value.Stage)
                ? last?.Stage ?? "unknown"
                : value.Stage;
            if (last is not null && StageOrder(stage) < StageOrder(last.Stage))
            {
                stage = last.Stage;
            }

            long completedItems = Math.Max(0, value.CompletedItems);
            long totalItems = Math.Max(completedItems, value.TotalItems);
            long completedBytes = Math.Max(0, value.CompletedBytes);
            long totalBytes = Math.Max(completedBytes, value.TotalBytes);
            if (last is not null)
            {
                completedItems = Math.Max(completedItems, last.CompletedItems);
                totalItems = Math.Max(totalItems, last.TotalItems);
                completedBytes = Math.Max(completedBytes, last.CompletedBytes);
                totalBytes = Math.Max(totalBytes, last.TotalBytes);
            }

            OperationProgress normalized = new(stage, completedItems, totalItems, completedBytes, totalBytes);
            last = normalized;
            sink.Report(normalized);
        }
    }

    private static int StageOrder(string stage) => stage switch
    {
        "metadata" => 0,
        "preflight" => 1,
        "download" => 2,
        "verify" => 3,
        "commit" => 4,
        "auth" => 0,
        "java" => 1,
        "launch" => 2,
        "running" => 3,
        _ => int.MaxValue,
    };
}
