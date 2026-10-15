using System.Text.Json;
using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Blackbox.Infrastructure.Services;

public interface IInboxMessageProcessor
{
    Task<bool> RecordIfNewAsync(
        string consumerName,
        string messageId,
        string payload,
        CancellationToken cancellationToken = default);
}

public sealed class InboxMessageProcessor : IInboxMessageProcessor
{
    private readonly BlackboxDbContext _dbContext;

    public InboxMessageProcessor(BlackboxDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> RecordIfNewAsync(
        string consumerName,
        string messageId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        var insertedRows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inbox_messages (id, consumer_name, message_id, payload, status, created_at)
            VALUES ({Guid.NewGuid()}, {consumerName}, {messageId}, CAST({payload} AS jsonb), 'RECEIVED', {DateTime.UtcNow})
            ON CONFLICT (consumer_name, message_id) DO NOTHING
            """, cancellationToken);

        return insertedRows == 1;
    }

    public static string ResolveMessageId(string payload, string transportMessageId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "EventId", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String
                        && Guid.TryParse(property.Value.GetString(), out var eventId))
                    {
                        return eventId.ToString("D");
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Preserve the transport-coordinate fallback for payloads that are not domain-event JSON.
        }

        return transportMessageId;
    }
}
