using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blackbox.Infrastructure.Services;

public enum InboxProcessingResult
{
    Processed,
    ReceiptRecorded,
    Duplicate,
    Quarantined
}

public interface IInboxMessageProcessor
{
    Task<InboxProcessingResult> ProcessAsync(
        string consumerName,
        string transportMessageId,
        string payload,
        CancellationToken cancellationToken = default);
}

public sealed class InboxMessageProcessor : IInboxMessageProcessor
{
    private const int MaxConsumerNameLength = 64;
    private const int MaxMessageIdLength = 128;
    private const int MaxEventTypeLength = 64;
    private const int MaxCorrelationIdLength = 128;

    private readonly BlackboxDbContext _dbContext;

    public InboxMessageProcessor(BlackboxDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<InboxProcessingResult> ProcessAsync(
        string consumerName,
        string transportMessageId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportMessageId);
        ArgumentNullException.ThrowIfNull(payload);

        if (consumerName.Length > MaxConsumerNameLength)
            throw new ArgumentOutOfRangeException(nameof(consumerName), $"Consumer name must be at most {MaxConsumerNameLength} characters.");
        if (transportMessageId.Length > MaxMessageIdLength)
            throw new ArgumentOutOfRangeException(nameof(transportMessageId), $"Transport message ID must be at most {MaxMessageIdLength} characters.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            return await QuarantineAsync(
                source: "InboxInvalidJson",
                transportMessageId: transportMessageId,
                workflowId: null,
                payload: SerializeRawPayload(payload),
                exceptionMessage: $"Kafka payload is not valid JSON: {ex.Message}",
                deduplicationKey: TransportDeduplicationKey(consumerName, transportMessageId),
                cancellationToken);
        }

        using (document)
        {
            if (ContainsNullCharacter(document.RootElement))
            {
                return await QuarantineAsync(
                    source: "InboxUnsupportedJson",
                    transportMessageId: transportMessageId,
                    workflowId: null,
                    payload: SerializeRawPayload(payload),
                    exceptionMessage: "Kafka payload contains a null character that PostgreSQL jsonb cannot store.",
                    deduplicationKey: TransportDeduplicationKey(consumerName, transportMessageId),
                    cancellationToken);
            }

            if (ContainsDuplicateProperties(document.RootElement))
            {
                return await QuarantineAsync(
                    source: "InboxAmbiguousJson",
                    transportMessageId: transportMessageId,
                    workflowId: null,
                    payload: SerializeRawPayload(payload),
                    exceptionMessage: "Kafka payload contains duplicate JSON property names and cannot be interpreted unambiguously.",
                    deduplicationKey: TransportDeduplicationKey(consumerName, transportMessageId),
                    cancellationToken);
            }

            if (!TryReadEnvelope(document.RootElement, out var envelope, out var eventId, out var envelopeError))
            {
                return await QuarantineAsync(
                    source: "InboxInvalidEvent",
                    transportMessageId: transportMessageId,
                    workflowId: TryReadGuid(document.RootElement, "WorkflowId", out var invalidWorkflowId) ? invalidWorkflowId : null,
                    payload,
                    exceptionMessage: envelopeError ?? "Kafka event envelope is invalid.",
                    deduplicationKey: eventId == Guid.Empty
                        ? TransportDeduplicationKey(consumerName, transportMessageId)
                        : $"inbox-invalid-event:{consumerName}:{eventId:D}",
                    cancellationToken);
            }

            // Domain EventId is canonical. Messages without one retain the transport-coordinate fallback.
            var messageId = eventId == Guid.Empty ? transportMessageId : eventId.ToString("D");
            var inboxStatus = envelope is null ? "RECEIVED" : "PROCESSED";
            if (messageId.Length > MaxMessageIdLength)
            {
                return await QuarantineAsync(
                    source: "InboxInvalidEvent",
                    transportMessageId: transportMessageId,
                    workflowId: envelope?.WorkflowId,
                    payload,
                    exceptionMessage: "Resolved Kafka message ID exceeds the Inbox column limit.",
                    deduplicationKey: TransportDeduplicationKey(consumerName, transportMessageId),
                    cancellationToken);
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var insertedRows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO inbox_messages (id, consumer_name, message_id, payload, status, created_at)
                    VALUES ({Guid.NewGuid()}, {consumerName}, {messageId}, CAST({payload} AS jsonb), {inboxStatus}, {DateTime.UtcNow})
                    ON CONFLICT (consumer_name, message_id) DO NOTHING
                    """, cancellationToken);

                if (insertedRows == 0)
                {
                    var existingPayloadMatches = await _dbContext.Database.SqlQuery<bool>($"""
                        SELECT payload = CAST({payload} AS jsonb) AS "Value"
                        FROM inbox_messages
                        WHERE consumer_name = {consumerName} AND message_id = {messageId}
                        """).SingleAsync(cancellationToken);

                    if (existingPayloadMatches)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return InboxProcessingResult.Duplicate;
                    }

                    await InsertDeadLetterAsync(
                        source: "InboxEventConflict",
                        transportMessageId: transportMessageId,
                        workflowId: envelope?.WorkflowId,
                        payload,
                        exceptionMessage: "The same Inbox EventId was received with a different JSON payload.",
                        deduplicationKey: $"inbox-event-conflict:{consumerName}:{messageId}",
                        cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                    return InboxProcessingResult.Quarantined;
                }

                if (envelope is not null)
                    await ApplyProjectionAsync(envelope, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return envelope is null ? InboxProcessingResult.ReceiptRecorded : InboxProcessingResult.Processed;
            }
            catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await QuarantineAsync(
                    source: "InboxUnsupportedJson",
                    transportMessageId: transportMessageId,
                    workflowId: envelope?.WorkflowId,
                    payload: SerializeRawPayload(payload),
                    exceptionMessage: $"PostgreSQL could not store the Kafka JSON value: {ex.MessageText}",
                    deduplicationKey: envelope is null
                        ? TransportDeduplicationKey(consumerName, transportMessageId)
                        : $"inbox-unsupported-json:{consumerName}:{envelope.EventId:D}",
                    cancellationToken);
            }
        }
    }

    public static string ResolveMessageId(string payload, string transportMessageId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryReadGuid(document.RootElement, "EventId", out var eventId)
                ? eventId.ToString("D")
                : transportMessageId;
        }
        catch (JsonException)
        {
            return transportMessageId;
        }
    }

    private async Task<InboxProcessingResult> QuarantineAsync(
        string source,
        string transportMessageId,
        Guid? workflowId,
        string payload,
        string exceptionMessage,
        string deduplicationKey,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await InsertDeadLetterAsync(source, transportMessageId, workflowId, payload, exceptionMessage, deduplicationKey, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InboxProcessingResult.Quarantined;
    }

    private Task<int> InsertDeadLetterAsync(
        string source,
        string transportMessageId,
        Guid? workflowId,
        string payload,
        string exceptionMessage,
        string deduplicationKey,
        CancellationToken cancellationToken) =>
        _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO dead_letter_messages
                (id, workflow_id, transport_message_id, deduplication_key, source, payload, exception_message, stack_trace, status, created_at, replayed_at)
            VALUES
                ({Guid.NewGuid()}, {workflowId}, {transportMessageId}, {deduplicationKey}, {source}, CAST({payload} AS jsonb),
                 {exceptionMessage}, NULL, 'Quarantined', {DateTime.UtcNow}, NULL)
            ON CONFLICT (deduplication_key) WHERE deduplication_key IS NOT NULL DO NOTHING
            """, cancellationToken);

    private Task<int> ApplyProjectionAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken) =>
        _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_event_projections
                (workflow_id, correlation_id, event_count, last_event_id, last_event_type,
                 last_occurred_at_utc, updated_at_utc)
            VALUES
                ({envelope.WorkflowId}, {envelope.CorrelationId}, 1, {envelope.EventId}, {envelope.EventType},
                 {envelope.OccurredAtUtc}, {DateTime.UtcNow})
            ON CONFLICT (workflow_id) DO UPDATE SET
                correlation_id = COALESCE(EXCLUDED.correlation_id, workflow_event_projections.correlation_id),
                event_count = workflow_event_projections.event_count + 1,
                last_event_id = CASE
                    WHEN (EXCLUDED.last_occurred_at_utc, EXCLUDED.last_event_id) >=
                         (workflow_event_projections.last_occurred_at_utc, workflow_event_projections.last_event_id)
                    THEN EXCLUDED.last_event_id ELSE workflow_event_projections.last_event_id END,
                last_event_type = CASE
                    WHEN (EXCLUDED.last_occurred_at_utc, EXCLUDED.last_event_id) >=
                         (workflow_event_projections.last_occurred_at_utc, workflow_event_projections.last_event_id)
                    THEN EXCLUDED.last_event_type ELSE workflow_event_projections.last_event_type END,
                last_occurred_at_utc = GREATEST(
                    EXCLUDED.last_occurred_at_utc, workflow_event_projections.last_occurred_at_utc),
                updated_at_utc = EXCLUDED.updated_at_utc
            """, cancellationToken);

    private static bool TryReadEnvelope(
        JsonElement root,
        out EventEnvelope? envelope,
        out Guid eventId,
        out string? error)
    {
        envelope = null;
        eventId = Guid.Empty;
        error = null;

        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "EventId", out var eventIdElement))
            return true;

        if (eventIdElement.ValueKind != JsonValueKind.String || !Guid.TryParse(eventIdElement.GetString(), out eventId) || eventId == Guid.Empty)
        {
            error = "EventId must be a non-empty GUID when present.";
            return false;
        }

        if (!TryReadGuid(root, "WorkflowId", out var workflowId))
        {
            error = "A domain event with EventId must include a valid WorkflowId.";
            return false;
        }

        if (!TryGetProperty(root, "EventType", out var eventTypeElement)
            || eventTypeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(eventTypeElement.GetString())
            || eventTypeElement.GetString()!.Length > MaxEventTypeLength)
        {
            error = $"A domain event must include a non-empty EventType of at most {MaxEventTypeLength} characters.";
            return false;
        }

        if (!TryGetProperty(root, "OccurredOnUtc", out var occurredAtElement)
            || occurredAtElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                occurredAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var occurredAt))
        {
            error = "A domain event must include a valid OccurredOnUtc timestamp.";
            return false;
        }

        string? correlationId = null;
        if (TryGetProperty(root, "CorrelationId", out var correlationElement)
            && correlationElement.ValueKind != JsonValueKind.Null)
        {
            if (correlationElement.ValueKind != JsonValueKind.String
                || correlationElement.GetString() is not { Length: <= MaxCorrelationIdLength } correlationValue)
            {
                error = $"CorrelationId must be a string of at most {MaxCorrelationIdLength} characters when present.";
                return false;
            }

            correlationId = correlationValue;
        }

        envelope = new EventEnvelope(
            eventId,
            workflowId,
            eventTypeElement.GetString()!,
            occurredAt.UtcDateTime,
            correlationId);
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static bool TryReadGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value)
            && value != Guid.Empty;
    }

    private static bool ContainsNullCharacter(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Contains('\0') == true,
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsNullCharacter(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsNullCharacter),
        _ => false
    };

    private static bool ContainsDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicateProperties(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsDuplicateProperties);
        }

        return false;
    }

    private static string SerializeRawPayload(string payload) =>
        JsonSerializer.Serialize(new { rawPayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) });

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string TransportDeduplicationKey(string consumerName, string transportMessageId)
    {
        var identity = string.Concat(consumerName, "\n", transportMessageId);
        return $"inbox-transport:{Sha256Hex(identity)}";
    }

    private sealed record EventEnvelope(
        Guid EventId,
        Guid WorkflowId,
        string EventType,
        DateTime OccurredAtUtc,
        string? CorrelationId);
}
