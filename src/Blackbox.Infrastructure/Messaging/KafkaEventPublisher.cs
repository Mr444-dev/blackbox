using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(string topic, string messageKey, string payload, CancellationToken cancellationToken = default);
}

public class KafkaEventPublisher : IEventPublisher
{
    private readonly IProducer<string, string>? _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(ILogger<KafkaEventPublisher> logger, IProducer<string, string>? producer = null)
    {
        _logger = logger;
        _producer = producer;
    }

    public async Task PublishAsync(string topic, string messageKey, string payload, CancellationToken cancellationToken = default)
    {
        if (_producer == null)
        {
            _logger.LogWarning("Kafka producer not initialized. Emulating publishing to topic '{Topic}' for key '{Key}'.", 
                topic, messageKey);
            return;
        }

        var message = new Message<string, string>
        {
            Key = messageKey,
            Value = payload,
            Timestamp = new Timestamp(DateTime.UtcNow)
        };

        var deliveryResult = await _producer.ProduceAsync(topic, message, cancellationToken);
        _logger.LogInformation("Delivered message to {Topic} [Partition: {Partition}, Offset: {Offset}].",
            deliveryResult.Topic, deliveryResult.Partition.Value, deliveryResult.Offset.Value);
    }
}
