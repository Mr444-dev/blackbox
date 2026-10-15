using Blackbox.Core.Domain;
using Blackbox.Core.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Blackbox.Infrastructure.Persistence;

public class BlackboxDbContext : DbContext
{
    public DbSet<WorkflowInstance> Workflows => Set<WorkflowInstance>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceReservation> ResourceReservations => Set<ResourceReservation>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<DeadLetterMessage> DeadLetterMessages => Set<DeadLetterMessage>();
    public DbSet<WorkflowEventProjection> WorkflowEventProjections => Set<WorkflowEventProjection>();

    public BlackboxDbContext(DbContextOptions<BlackboxDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Workflows
        modelBuilder.Entity<WorkflowInstance>(entity =>
        {
            entity.ToTable("workflows");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).HasColumnName("id");
            entity.Property(w => w.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
            entity.HasIndex(w => w.CorrelationId).IsUnique();
            entity.Property(w => w.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(w => w.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            entity.Property(w => w.ErrorDetailsJson).HasColumnName("error_details").HasColumnType("jsonb");
            entity.Property(w => w.Version).HasColumnName("version").IsRequired();
            entity.Property(w => w.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(w => w.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Ignore(w => w.DomainEvents);
        });

        // Resources
        modelBuilder.Entity<Resource>(entity =>
        {
            entity.ToTable("resources");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            entity.Property(r => r.AvailableQuantity).HasColumnName("available_quantity").IsRequired();
            entity.Property(r => r.AllocatedQuantity).HasColumnName("allocated_quantity").IsRequired();
            entity.Property(r => r.ConsumedQuantity).HasColumnName("consumed_quantity").HasDefaultValue(0).IsRequired();
            entity.Ignore(r => r.TotalCapacity);
            entity.Property(r => r.Version).HasColumnName("version").IsRequired();
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        // Resource Reservations
        modelBuilder.Entity<ResourceReservation>(entity =>
        {
            entity.ToTable("resource_reservations");
            entity.HasKey(rr => rr.Id);
            entity.Property(rr => rr.Id).HasColumnName("id");
            entity.Property(rr => rr.ResourceId).HasColumnName("resource_id").HasMaxLength(64).IsRequired();
            entity.Property(rr => rr.WorkflowId).HasColumnName("workflow_id").IsRequired();
            entity.Property(rr => rr.Quantity).HasColumnName("quantity").IsRequired();
            entity.Property(rr => rr.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(rr => rr.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(rr => new { rr.ResourceId, rr.WorkflowId }).IsUnique();
        });

        // Outbox Messages
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).HasColumnName("id");
            entity.Property(o => o.Topic).HasColumnName("topic").HasMaxLength(128).IsRequired();
            entity.Property(o => o.MessageKey).HasColumnName("message_key").HasMaxLength(128).IsRequired();
            entity.Property(o => o.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
            entity.Property(o => o.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            entity.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(o => o.RetryCount).HasColumnName("retry_count").IsRequired();
            entity.Property(o => o.Error).HasColumnName("error");
            entity.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(o => o.PublishedAt).HasColumnName("published_at");
        });

        // Inbox Messages
        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).HasColumnName("id");
            entity.Property(i => i.ConsumerName).HasColumnName("consumer_name").HasMaxLength(64).IsRequired();
            entity.Property(i => i.MessageId).HasColumnName("message_id").HasMaxLength(128).IsRequired();
            entity.Property(i => i.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            entity.Property(i => i.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            entity.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(i => new { i.ConsumerName, i.MessageId }).IsUnique();
        });

        // Dead Letter Messages
        modelBuilder.Entity<DeadLetterMessage>(entity =>
        {
            entity.ToTable("dead_letter_messages");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Id).HasColumnName("id");
            entity.Property(d => d.WorkflowId).HasColumnName("workflow_id");
            entity.Property(d => d.TransportMessageId).HasColumnName("transport_message_id").HasMaxLength(128);
            entity.Property(d => d.DeduplicationKey).HasColumnName("deduplication_key").HasMaxLength(128);
            entity.Property(d => d.Source).HasColumnName("source").HasMaxLength(64).IsRequired();
            entity.Property(d => d.Payload).HasColumnType("jsonb").HasColumnName("payload").IsRequired();
            entity.Property(d => d.ExceptionMessage).HasColumnName("exception_message").IsRequired();
            entity.Property(d => d.StackTrace).HasColumnName("stack_trace");
            entity.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(d => d.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(d => d.ReplayedAt).HasColumnName("replayed_at");
            entity.HasIndex(d => d.DeduplicationKey)
                .IsUnique()
                .HasFilter("deduplication_key IS NOT NULL");
        });

        modelBuilder.Entity<WorkflowEventProjection>(entity =>
        {
            entity.ToTable("workflow_event_projections");
            entity.HasKey(projection => projection.WorkflowId);
            entity.Property(projection => projection.WorkflowId).HasColumnName("workflow_id");
            entity.Property(projection => projection.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(projection => projection.EventCount).HasColumnName("event_count").IsRequired();
            entity.Property(projection => projection.LastEventId).HasColumnName("last_event_id").IsRequired();
            entity.Property(projection => projection.LastEventType).HasColumnName("last_event_type").HasMaxLength(64).IsRequired();
            entity.Property(projection => projection.LastOccurredAtUtc).HasColumnName("last_occurred_at_utc").IsRequired();
            entity.Property(projection => projection.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        });
    }
}
