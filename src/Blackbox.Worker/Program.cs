using Blackbox.Infrastructure.Messaging;
using Blackbox.Infrastructure.Persistence;
using Blackbox.Infrastructure.Services;
using Blackbox.Worker;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");

builder.Services.AddDbContext<BlackboxDbContext>(options =>
    options.UseNpgsql(connectionString));

var kafkaBootstrapServers = builder.Configuration["Kafka:BootstrapServers"];
if (string.IsNullOrWhiteSpace(kafkaBootstrapServers))
    throw new InvalidOperationException("Kafka:BootstrapServers must be configured.");
builder.Services.AddSingleton<IProducer<string, string>>(_ => new ProducerBuilder<string, string>(
    new ProducerConfig { BootstrapServers = kafkaBootstrapServers }).Build());
builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
builder.Services.AddScoped<IOutboxProcessor, OutboxProcessor>();

builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<KafkaInboxConsumer>();

var host = builder.Build();
host.Run();
