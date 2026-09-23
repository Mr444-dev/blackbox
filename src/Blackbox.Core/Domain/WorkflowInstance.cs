using Blackbox.Core.Domain.Events;

namespace Blackbox.Core.Domain;

public class WorkflowInstance
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public Guid Id { get; private set; }
    public string CorrelationId { get; private set; }
    public WorkflowStatus Status { get; private set; }
    public string PayloadJson { get; private set; }
    public string? ErrorDetailsJson { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    // Required for ORM reflection
    private WorkflowInstance()
    {
        CorrelationId = string.Empty;
        PayloadJson = "{}";
    }

    public static WorkflowInstance Create(string correlationId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new WorkflowInvariantException("CorrelationIdRequired", "CorrelationId cannot be null or empty.");

        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new WorkflowInvariantException("PayloadRequired", "Payload cannot be null or empty.");

        var instance = new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            Status = WorkflowStatus.Created,
            PayloadJson = payloadJson,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        instance._domainEvents.Add(new WorkflowCreatedEvent(instance.Id, instance.CorrelationId, instance.PayloadJson));
        return instance;
    }

    public void Validate()
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Validated);

        Status = WorkflowStatus.Validated;
        Touch();
        _domainEvents.Add(new WorkflowValidatedEvent(Id, CorrelationId));
    }

    public void Reserve()
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Reserved);

        Status = WorkflowStatus.Reserved;
        Touch();
    }

    public void StartProcessing()
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Processing);

        Status = WorkflowStatus.Processing;
        Touch();
        _domainEvents.Add(new WorkflowProcessingStartedEvent(Id, CorrelationId));
    }

    public void AwaitCallback(string callbackReference)
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.WaitingCallback);

        Status = WorkflowStatus.WaitingCallback;
        Touch();
        _domainEvents.Add(new WorkflowAwaitingCallbackEvent(Id, CorrelationId, callbackReference));
    }

    public void Complete()
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Completed);

        Status = WorkflowStatus.Completed;
        Touch();
        _domainEvents.Add(new WorkflowCompletedEvent(Id, CorrelationId, UpdatedAt));
    }

    public void Fail(string reason, string? errorDetailsJson = null)
    {
        EnsureNotTerminal();

        Status = WorkflowStatus.Failed;
        ErrorDetailsJson = errorDetailsJson ?? $"{{\"reason\":\"{reason}\"}}";
        Touch();
        _domainEvents.Add(new WorkflowFailedEvent(Id, CorrelationId, reason));
    }

    public void Cancel(string reason)
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Cancelled);

        Status = WorkflowStatus.Cancelled;
        ErrorDetailsJson = $"{{\"cancellation_reason\":\"{reason}\"}}";
        Touch();
        _domainEvents.Add(new WorkflowCancelledEvent(Id, CorrelationId, reason));
    }

    public void StartCompensation()
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Compensating);

        Status = WorkflowStatus.Compensating;
        Touch();
    }

    public void FinishCompensation()
    {
        EnsureNotTerminal();
        EnsureTransitionAllowed(WorkflowStatus.Compensated);

        Status = WorkflowStatus.Compensated;
        Touch();
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void EnsureNotTerminal()
    {
        if (IsTerminalState(Status))
        {
            throw new WorkflowInvariantException(
                "TerminalStateImmutability",
                $"Workflow is in terminal state '{Status}' and cannot undergo further state transitions.");
        }
    }

    public static bool IsTerminalState(WorkflowStatus status) =>
        status is WorkflowStatus.Completed 
               or WorkflowStatus.Failed 
               or WorkflowStatus.Cancelled 
               or WorkflowStatus.Compensated;

    private void EnsureTransitionAllowed(WorkflowStatus target)
    {
        bool allowed = (Status, target) switch
        {
            (WorkflowStatus.Created, WorkflowStatus.Validated) => true,
            (WorkflowStatus.Created, WorkflowStatus.Cancelled) => true,
            (WorkflowStatus.Validated, WorkflowStatus.Reserved) => true,
            (WorkflowStatus.Validated, WorkflowStatus.Cancelled) => true,
            (WorkflowStatus.Reserved, WorkflowStatus.Processing) => true,
            (WorkflowStatus.Reserved, WorkflowStatus.Compensating) => true,
            (WorkflowStatus.Reserved, WorkflowStatus.Cancelled) => true,
            (WorkflowStatus.Processing, WorkflowStatus.WaitingCallback) => true,
            (WorkflowStatus.Processing, WorkflowStatus.Completed) => true,
            (WorkflowStatus.Processing, WorkflowStatus.Compensating) => true,
            (WorkflowStatus.WaitingCallback, WorkflowStatus.Completed) => true,
            (WorkflowStatus.WaitingCallback, WorkflowStatus.Compensating) => true,
            (WorkflowStatus.WaitingCallback, WorkflowStatus.Cancelled) => true,
            (WorkflowStatus.Compensating, WorkflowStatus.Compensated) => true,
            _ => false
        };

        if (!allowed)
        {
            throw new WorkflowInvariantException(
                Status,
                target,
                $"Direct transition from '{Status}' to '{target}' is forbidden by the workflow state machine.");
        }
    }

    private void Touch()
    {
        UpdatedAt = DateTime.UtcNow;
        Version++;
    }
}
