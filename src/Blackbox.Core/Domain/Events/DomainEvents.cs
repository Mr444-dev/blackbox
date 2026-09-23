namespace Blackbox.Core.Domain.Events;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredOnUtc { get; }
    string EventType { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredOnUtc { get; init; } = DateTime.UtcNow;
    public abstract string EventType { get; }
}

public record WorkflowCreatedEvent(Guid WorkflowId, string CorrelationId, string PayloadJson) : DomainEvent
{
    public override string EventType => "WorkflowCreated.v1";
}

public record WorkflowValidatedEvent(Guid WorkflowId, string CorrelationId) : DomainEvent
{
    public override string EventType => "WorkflowValidated.v1";
}

public record ResourceReservedEvent(Guid WorkflowId, string ResourceId, int Quantity) : DomainEvent
{
    public override string EventType => "ResourceReserved.v1";
}

public record WorkflowProcessingStartedEvent(Guid WorkflowId, string CorrelationId) : DomainEvent
{
    public override string EventType => "WorkflowProcessingStarted.v1";
}

public record WorkflowAwaitingCallbackEvent(Guid WorkflowId, string CorrelationId, string CallbackReference) : DomainEvent
{
    public override string EventType => "WorkflowAwaitingCallback.v1";
}

public record WorkflowCompletedEvent(Guid WorkflowId, string CorrelationId, DateTime CompletedAtUtc) : DomainEvent
{
    public override string EventType => "WorkflowCompleted.v1";
}

public record WorkflowFailedEvent(Guid WorkflowId, string CorrelationId, string Reason) : DomainEvent
{
    public override string EventType => "WorkflowFailed.v1";
}

public record WorkflowCancelledEvent(Guid WorkflowId, string CorrelationId, string Reason) : DomainEvent
{
    public override string EventType => "WorkflowCancelled.v1";
}
