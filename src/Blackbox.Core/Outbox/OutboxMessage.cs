namespace Blackbox.Core.Outbox;

public enum OutboxStatus
{
    Pending,
    Published,
    Failed
}

public class OutboxMessage
{
    public Guid Id { get; private set; }
    public string Topic { get; private set; }
    public string MessageKey { get; private set; }
    public string EventType { get; private set; }
    public string Payload { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? PublishedAt { get; private set; }

    private OutboxMessage()
    {
        Topic = string.Empty;
        MessageKey = string.Empty;
        EventType = string.Empty;
        Payload = "{}";
    }

    public static OutboxMessage Create(string topic, string messageKey, string eventType, string payload)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            MessageKey = messageKey,
            EventType = eventType,
            Payload = payload,
            Status = OutboxStatus.Pending,
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkPublished()
    {
        Status = OutboxStatus.Published;
        PublishedAt = DateTime.UtcNow;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        Status = OutboxStatus.Failed;
        RetryCount++;
        Error = error;
    }
}

public class InboxMessage
{
    public Guid Id { get; private set; }
    public string ConsumerName { get; private set; }
    public string MessageId { get; private set; }
    public string Payload { get; private set; }
    public string Status { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private InboxMessage()
    {
        ConsumerName = string.Empty;
        MessageId = string.Empty;
        Payload = "{}";
        Status = "PROCESSED";
    }

    public static InboxMessage Create(string consumerName, string messageId, string payload)
    {
        return new InboxMessage
        {
            Id = Guid.NewGuid(),
            ConsumerName = consumerName,
            MessageId = messageId,
            Payload = payload,
            Status = "PROCESSED",
            CreatedAt = DateTime.UtcNow
        };
    }
}

public enum DeadLetterStatus
{
    Quarantined,
    Replayed,
    Discarded
}

public class DeadLetterMessage
{
    public Guid Id { get; private set; }
    public Guid? WorkflowId { get; private set; }
    public string Source { get; private set; }
    public string Payload { get; private set; }
    public string ExceptionMessage { get; private set; }
    public string? StackTrace { get; private set; }
    public DeadLetterStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ReplayedAt { get; private set; }

    private DeadLetterMessage()
    {
        Source = string.Empty;
        Payload = "{}";
        ExceptionMessage = string.Empty;
    }

    public static DeadLetterMessage Create(Guid? workflowId, string source, string payload, string exceptionMessage, string? stackTrace = null)
    {
        return new DeadLetterMessage
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Source = source,
            Payload = payload,
            ExceptionMessage = exceptionMessage,
            StackTrace = stackTrace,
            Status = DeadLetterStatus.Quarantined,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkReplayed()
    {
        Status = DeadLetterStatus.Replayed;
        ReplayedAt = DateTime.UtcNow;
    }

    public void Discard()
    {
        Status = DeadLetterStatus.Discarded;
    }
}
