using Lacertae.Desktop.ViewModels.Tasks;
using Lacertae.Domain.Operations;

namespace Lacertae.Desktop.Tests.Tasks;

public sealed class TasksViewModelTests
{
    [Fact]
    public void SnapshotExposesProgressAndBlocksCancelDuringCommit()
    {
        TasksViewModel viewModel = new([new OperationSnapshot("op-1", "install", OperationState.Running, new OperationProgress("commit", 1, 2, 100, 200), "DOWNLOAD_FAILED")]);
        TaskItemViewModel item = Assert.Single(viewModel.Items);
        Assert.Equal("install", item.Kind);
        Assert.Equal("commit", item.Stage);
        Assert.Equal("op-1", item.SourceId);
        Assert.False(item.CanCancel);
        Assert.True(item.IsRetryableProblem);
    }

    [Fact]
    public void CompletedHistoryCanBeClearedWithoutStoreMutation()
    {
        TasksViewModel viewModel = new([new OperationSnapshot("op-1", "install", OperationState.Succeeded, null, null)]);
        viewModel.ClearCompleted();
        Assert.Empty(viewModel.Items);
    }
}
