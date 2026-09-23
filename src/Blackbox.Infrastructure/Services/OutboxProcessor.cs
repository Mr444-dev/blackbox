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
        var pendingMessages = await _dbContext.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pendingMessages.Count == 0)
            return 0;

        _logger.LogInformation("Processing {Count} pending outbox messages.", pendingMessages.Count);
        int processedCount = 0;

        foreach (var message in pendingMessages)
        {
            try
            {
                await _eventPublisher.PublishAsync(message.Topic, message.MessageKey, message.Payload, ct);
                message.MarkPublished();
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish outbox message {MessageId} to topic {Topic}.", message.Id, message.Topic);
                message.MarkFailed(ex.Message);

                // If repeated failures exceed threshold, record in DLQ
                if (message.RetryCount >= 5)
                {
                    var dlqMessage = DeadLetterMessage.Create(
                        workflowId: null,
                        source: "OutboxProcessor",
                        payload: message.Payload,
                        exceptionMessage: $"Outbox dispatch exceeded retry threshold: {ex.Message}",
                        stackTrace: ex.StackTrace
                    );
                    await _dbContext.DeadLetterMessages.AddAsync(dlqMessage, ct);
                }
            }
        }

        await _dbContext.SaveChangesAsync(ct);
        return processedCount;
    }
}
