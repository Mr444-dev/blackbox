using Blackbox.Core.Outbox;
using Blackbox.Infrastructure.Messaging;
using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure.Services;

public interface IOutboxProcessor
{
    Task<int> ProcessPendingMessagesAsync(int batchSize = 50, CancellationToken ct = default);
}

public class OutboxProcessor : IOutboxProcessor
{
    private readonly BlackboxDbContext _dbContext;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        BlackboxDbContext dbContext,
        IEventPublisher eventPublisher,
        ILogger<OutboxProcessor> logger)
    {
        _dbContext = dbContext;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<int> ProcessPendingMessagesAsync(int batchSize = 50, CancellationToken ct = default)
    {
        int processedCount = 0;
        for (var i = 0; i < batchSize; i++)
        {
            // Hold the row lock through broker acknowledgement and the database update.
            // If the process crashes before commit, PostgreSQL rolls back and the row is retried.
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
            var messages = await _dbContext.OutboxMessages
                .FromSqlRaw("""
                    SELECT * FROM outbox_messages
                    WHERE status = 'Pending'
                    ORDER BY created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);
            var message = messages.FirstOrDefault();

            if (message is null)
            {
                await transaction.CommitAsync(ct);
                break;
            }

            message.MarkProcessing();
            try
            {
                await _eventPublisher.PublishAsync(message.Topic, message.MessageKey, message.Payload, ct);
                message.MarkPublished();
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish outbox message {MessageId} to topic {Topic}.", message.Id, message.Topic);
                message.MarkRetryableFailure(ex.Message, maxAttempts: 5);

                if (message.Status == OutboxStatus.Failed)
                {
                    Guid? workflowId = Guid.TryParse(message.MessageKey, out var parsedWorkflowId)
                        ? parsedWorkflowId
                        : null;
                    var dlqMessage = DeadLetterMessage.Create(
                        workflowId,
                        source: "OutboxProcessor",
                        payload: message.Payload,
                        exceptionMessage: $"Outbox dispatch exceeded retry threshold: {ex.Message}",
                        stackTrace: ex.StackTrace
                    );
                    await _dbContext.DeadLetterMessages.AddAsync(dlqMessage, ct);
                }
            }

            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return processedCount;
    }
}
