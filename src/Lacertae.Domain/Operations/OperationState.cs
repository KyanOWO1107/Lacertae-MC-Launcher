namespace Lacertae.Domain.Operations;

public enum OperationState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}
