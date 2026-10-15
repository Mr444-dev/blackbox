namespace Blackbox.Infrastructure.Persistence;

/// <summary>
/// Read model maintained by the Kafka Inbox. It is not the authoritative workflow state.
/// </summary>
public sealed class WorkflowEventProjection
{
    public Guid WorkflowId { get; private set; }
    public string? CorrelationId { get; private set; }
    public long EventCount { get; private set; }
    public Guid LastEventId { get; private set; }
    public string LastEventType { get; private set; }
    public DateTime LastOccurredAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private WorkflowEventProjection()
    {
        LastEventType = string.Empty;
    }
}
