namespace Blackbox.Core.Domain;

public enum WorkflowStatus
{
    Created,
    Validated,
    Reserved,
    Processing,
    WaitingCallback,
    Completed,
    Failed,
    Cancelled,
    Compensating,
    Compensated
}
