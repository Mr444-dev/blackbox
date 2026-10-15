using Blackbox.Infrastructure.Services;
using Confluent.Kafka;

namespace Blackbox.Worker;

public sealed class KafkaInboxConsumer : BackgroundService
{
    // The v2 group uses logical domain EventIds rather than broker coordinates for deduplication.
    private const string ConsumerName = "blackbox-inbox-v2";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaInboxConsumer> _logger;
    private readonly string _bootstrapServers;

    public KafkaInboxConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<KafkaInboxConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka:BootstrapServers must be configured.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = ConsumerName,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(["blackbox.workflow.events", "blackbox.resource.events"]);
        _logger.LogInformation("Inbox consumer subscribed to workflow and resource event topics.");

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? record;
            try
            {
                record = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (record is null || record.IsPartitionEOF)
                continue;

            var transportMessageId = $"{record.Topic}:{record.Partition.Value}:{record.Offset.Value}";
            var payload = record.Message.Value ?? string.Empty;
            var messageId = InboxMessageProcessor.ResolveMessageId(payload, transportMessageId);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IInboxMessageProcessor>();
                var result = await processor.ProcessAsync(
                    ConsumerName,
                    transportMessageId,
                    payload,
                    stoppingToken);

                if (result == InboxProcessingResult.Processed)
                {
                    _logger.LogInformation("Processed Kafka event {MessageId} into the workflow event projection.", messageId);
                }
                else if (result == InboxProcessingResult.ReceiptRecorded)
                {
                    _logger.LogInformation(
                        "Recorded Kafka receipt {MessageId}; no EventId was present for workflow projection.",
                        messageId);
                }
                else if (result == InboxProcessingResult.Duplicate)
                {
                    _logger.LogInformation(
                        "Skipping duplicate Kafka delivery for event {MessageId}; committing its offset.",
                        messageId);
                }
                else
                {
                    _logger.LogWarning(
                        "Quarantined Kafka message {MessageId}; committing its offset after durable quarantine.",
                        messageId);
                }

                // Commit only after the projection, duplicate check, or quarantine has committed in PostgreSQL.
                consumer.Commit(record);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to persist Kafka message {MessageId}; seeking it for redelivery.", messageId);
                consumer.Seek(record.TopicPartitionOffset);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
