using Blackbox.Infrastructure.Messaging;
using Blackbox.Infrastructure.Persistence;
using Blackbox.Infrastructure.Services;
using Blackbox.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Port=5432;Database=blackbox_db;Username=blackbox_user;Password=[REDACTED]";

builder.Services.AddDbContext<BlackboxDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
builder.Services.AddScoped<IOutboxProcessor, OutboxProcessor>();

builder.Services.AddHostedService<OutboxWorker>();

var host = builder.Build();
host.Run();
