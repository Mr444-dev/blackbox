namespace Blackbox.Core.Domain;

public class WorkflowInvariantException : Exception
{
    public string? InvariantName { get; }
    public WorkflowStatus? CurrentState { get; }
    public WorkflowStatus? AttemptedState { get; }

    public WorkflowInvariantException(string message) : base(message)
    {
    }

    public WorkflowInvariantException(string invariantName, string message) 
        : base($"[Invariant Violated: {invariantName}] {message}")
    {
        InvariantName = invariantName;
    }

    public WorkflowInvariantException(WorkflowStatus current, WorkflowStatus attempted, string message)
        : base($"[Illegal State Transition] Cannot transition from {current} to {attempted}. {message}")
    {
        CurrentState = current;
        AttemptedState = attempted;
    }
}
