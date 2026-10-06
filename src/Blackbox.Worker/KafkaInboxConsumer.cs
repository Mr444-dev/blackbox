using System.Text;
using Blackbox.Core.Outbox;
using Blackbox.Infrastructure.Persistence;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;

namespace Blackbox.Worker;

public sealed class KafkaInboxConsumer : BackgroundService
{
    private const string ConsumerName = "blackbox-inbox-v1";
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

            var messageId = $"{record.Topic}:{record.Partition.Value}:{record.Offset.Value}";
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BlackboxDbContext>();
                var alreadyRecorded = await db.InboxMessages.AnyAsync(
                    message => message.ConsumerName == ConsumerName && message.MessageId == messageId,
                    stoppingToken);

                if (!alreadyRecorded)
                {
                    await db.InboxMessages.AddAsync(
                        InboxMessage.Create(ConsumerName, messageId, record.Message.Value),
                        stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);
                }

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
