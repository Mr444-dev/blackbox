using System.Text.Json;
using System.Text;
using Blackbox.Core.Domain;
using Blackbox.Core.Domain.Events;
using Blackbox.Core.Outbox;
using Blackbox.Infrastructure.Messaging;
using Blackbox.Infrastructure.Persistence;
using Blackbox.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blackbox.IntegrationTests;

public class SpyEventPublisher : IEventPublisher
{
    public List<(string Topic, string Key, string Payload)> PublishedMessages { get; } = new();

    public Task PublishAsync(string topic, string messageKey, string payload, CancellationToken cancellationToken = default)
    {
        PublishedMessages.Add((topic, messageKey, payload));
        return Task.CompletedTask;
    }
}

public class TransactionalOutboxTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;
    public TransactionalOutboxTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [DockerFact]
    public async Task StartWorkflow_ShouldAtomicallyPersistWorkflowAndOutboxMessage()
    {

        // Arrange
        await using var dbContext = _fixture.CreateDbContext();
        var resourceRepo = new ResourceRepository(dbContext);
        var orchestrator = new WorkflowOrchestrator(dbContext, resourceRepo, NullLogger<WorkflowOrchestrator>.Instance);

        var correlationId = $"CORR-ATOM-{Guid.NewGuid():N}";
        var payload = "{\"orderId\":\"ORD-999\",\"amount\":299.99}";

        // Act
        var workflow = await orchestrator.StartWorkflowAsync(correlationId, payload);

        // Assert: Both workflow and outbox message must exist in PostgreSQL
        var persistedWorkflow = await dbContext.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflow.Id);
        persistedWorkflow.Should().NotBeNull();
        persistedWorkflow!.Status.Should().Be(WorkflowStatus.Created);
        persistedWorkflow.CorrelationId.Should().Be(correlationId);

        var outboxMessage = await dbContext.OutboxMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MessageKey == workflow.Id.ToString());

        outboxMessage.Should().NotBeNull();
        outboxMessage!.Status.Should().Be(OutboxStatus.Pending);
        outboxMessage.EventType.Should().Be("WorkflowCreated.v1");
        outboxMessage.Topic.Should().Be("blackbox.workflow.events");
        outboxMessage.PublishedAt.Should().BeNull();
    }

    [DockerFact]
    public async Task StartWorkflow_DuplicateRequest_ShouldReturnSameWorkflowWithoutDuplicateOutbox()
    {

        // Arrange
        await using var dbContext = _fixture.CreateDbContext();
        var resourceRepo = new ResourceRepository(dbContext);
        var orchestrator = new WorkflowOrchestrator(dbContext, resourceRepo, NullLogger<WorkflowOrchestrator>.Instance);

        var idempotencyKey = $"IDEM-DUP-{Guid.NewGuid():N}";
        var payload = "{\"item\":\"headphones\",\"price\":80.00}";

        // Act: Execute identical request twice
        var firstResult = await orchestrator.StartWorkflowAsync(idempotencyKey, payload);
        var secondResult = await orchestrator.StartWorkflowAsync(idempotencyKey, payload);

        // Assert: Idempotency guarantee
        secondResult.Id.Should().Be(firstResult.Id);
        secondResult.CorrelationId.Should().Be(firstResult.CorrelationId);

        var workflowCount = await dbContext.Workflows.CountAsync(w => w.CorrelationId == idempotencyKey);
        workflowCount.Should().Be(1);

        var outboxCount = await dbContext.OutboxMessages.CountAsync(m => m.MessageKey == firstResult.Id.ToString());
        outboxCount.Should().Be(1);
    }

    [DockerFact]
    public async Task StartWorkflow_ConcurrentDuplicateRequests_ShouldConvergeOnOneWorkflowAndOutbox()
    {
        var correlationId = $"IDEM-CONCURRENT-{Guid.NewGuid():N}";

        async Task<Guid> StartWithOwnContext()
        {
            await using var dbContext = _fixture.CreateDbContext();
            var orchestrator = new WorkflowOrchestrator(
                dbContext,
                new ResourceRepository(dbContext),
                NullLogger<WorkflowOrchestrator>.Instance);
            return (await orchestrator.StartWorkflowAsync(correlationId, "{\"order\":\"race\"}")).Id;
        }

        var workflowIds = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => StartWithOwnContext()));
        workflowIds.Distinct().Should().ContainSingle();

        await using var verificationContext = _fixture.CreateDbContext();
        (await verificationContext.Workflows.CountAsync(w => w.CorrelationId == correlationId)).Should().Be(1);
        (await verificationContext.OutboxMessages.CountAsync(m => m.MessageKey == workflowIds[0].ToString())).Should().Be(1);
    }

    [DockerFact]
    public async Task StartWorkflow_ReusedIdempotencyKeyWithDifferentPayload_ShouldBeRejected()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var orchestrator = new WorkflowOrchestrator(
            dbContext,
            new ResourceRepository(dbContext),
            NullLogger<WorkflowOrchestrator>.Instance);
        var key = $"IDEM-PAYLOAD-{Guid.NewGuid():N}";

        await orchestrator.StartWorkflowAsync(key, "{\"amount\":10}");
        var act = () => orchestrator.StartWorkflowAsync(key, "{\"amount\":99}");

        await act.Should().ThrowAsync<WorkflowInvariantException>()
            .WithMessage("*[Invariant Violated: IdempotencyKeyPayloadMismatch]*");
    }

    [DockerFact]
    public async Task OutboxProcessor_ShouldPublishPendingMessagesAndMarkThemPublished()
    {

        // Arrange
        await using var dbContext = _fixture.CreateDbContext();
        var resourceRepo = new ResourceRepository(dbContext);
        var orchestrator = new WorkflowOrchestrator(dbContext, resourceRepo, NullLogger<WorkflowOrchestrator>.Instance);

        var correlationId = $"CORR-DISPATCH-{Guid.NewGuid():N}";
        var workflow = await orchestrator.StartWorkflowAsync(correlationId, "{\"order\":\"dispatch-test\"}");

        var spyPublisher = new SpyEventPublisher();
        var processor = new OutboxProcessor(dbContext, spyPublisher, NullLogger<OutboxProcessor>.Instance);

        // Act
        int dispatched = await processor.ProcessPendingMessagesAsync(10);

        // Assert
        dispatched.Should().BeGreaterThan(0);
        spyPublisher.PublishedMessages.Should().Contain(m => m.Key == workflow.Id.ToString());

        var updatedMessage = await dbContext.OutboxMessages.FirstOrDefaultAsync(m => m.MessageKey == workflow.Id.ToString());
        updatedMessage.Should().NotBeNull();
        updatedMessage!.Status.Should().Be(OutboxStatus.Published);
        updatedMessage.PublishedAt.Should().NotBeNull();
    }


    [DockerFact]
    public async Task InboxProcessor_RedeliveredEventAtNewKafkaOffset_ShouldPersistOneReceipt()
    {
        var eventId = Guid.NewGuid();
        var domainEvent = new WorkflowCreatedEvent(
            Guid.NewGuid(),
            $"INBOX-REDELIVERY-{Guid.NewGuid():N}",
            "{\"orderId\":\"redelivery-test\"}")
        {
            EventId = eventId
        };
        var payload = JsonSerializer.Serialize(domainEvent);
        const string consumerName = "integration-test-inbox";

        var firstDeliveryId = InboxMessageProcessor.ResolveMessageId(
            payload,
            "blackbox.workflow.events:0:10");
        var redeliveryId = InboxMessageProcessor.ResolveMessageId(
            payload,
            "blackbox.workflow.events:0:11");

        firstDeliveryId.Should().Be(eventId.ToString("D"));
        redeliveryId.Should().Be(firstDeliveryId);

        // A fresh DbContext models a worker restart after the DB commit but before Kafka offset commit.
        await using (var firstContext = _fixture.CreateDbContext())
        {
            var firstDelivery = await new InboxMessageProcessor(firstContext)
                .ProcessAsync(consumerName, "blackbox.workflow.events:0:10", payload);
            firstDelivery.Should().Be(InboxProcessingResult.Processed);
        }

        await using (var restartedContext = _fixture.CreateDbContext())
        {
            var replayedDelivery = await new InboxMessageProcessor(restartedContext)
                .ProcessAsync(consumerName, "blackbox.workflow.events:0:11", payload);
            replayedDelivery.Should().Be(InboxProcessingResult.Duplicate);

            var receiptCount = await restartedContext.InboxMessages.CountAsync(message =>
                message.ConsumerName == consumerName &&
                message.MessageId == eventId.ToString("D"));
            receiptCount.Should().Be(1);

            var projection = await restartedContext.WorkflowEventProjections.SingleAsync(item => item.WorkflowId == domainEvent.WorkflowId);
            projection.EventCount.Should().Be(1);
            projection.LastEventId.Should().Be(eventId);
            projection.LastEventType.Should().Be(domainEvent.EventType);
        }
    }


    [DockerFact]
    public async Task InboxProcessor_ConcurrentDeliveriesOfSameEvent_ShouldInsertOnlyOneReceipt()
    {
        var eventId = Guid.NewGuid();
        var domainEvent = new WorkflowCreatedEvent(
            Guid.NewGuid(),
            $"INBOX-CONCURRENT-{Guid.NewGuid():N}",
            "{\"orderId\":\"concurrent-redelivery-test\"}")
        {
            EventId = eventId
        };
        var payload = JsonSerializer.Serialize(domainEvent);
        const string consumerName = "integration-test-concurrent-inbox";

        async Task<InboxProcessingResult> DeliverAsync(string transportMessageId)
        {
            await using var context = _fixture.CreateDbContext();
            return await new InboxMessageProcessor(context)
                .ProcessAsync(consumerName, transportMessageId, payload);
        }

        var results = await Task.WhenAll(
            DeliverAsync("blackbox.workflow.events:0:20"),
            DeliverAsync("blackbox.workflow.events:1:31"));

        results.Count(result => result == InboxProcessingResult.Processed).Should().Be(1);
        results.Count(result => result == InboxProcessingResult.Duplicate).Should().Be(1);

        await using var verificationContext = _fixture.CreateDbContext();
        var receiptCount = await verificationContext.InboxMessages.CountAsync(message =>
            message.ConsumerName == consumerName &&
            message.MessageId == eventId.ToString("D"));
        receiptCount.Should().Be(1);

        var projection = await verificationContext.WorkflowEventProjections.SingleAsync(item => item.WorkflowId == domainEvent.WorkflowId);
        projection.EventCount.Should().Be(1);
    }

    [DockerFact]
    public async Task InboxProcessor_OutOfOrderEvents_CountBothButKeepTheNewestProjection()
    {
        var workflowId = Guid.NewGuid();
        var correlationId = $"INBOX-ORDER-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var newerEvent = new WorkflowValidatedEvent(workflowId, correlationId)
        {
            EventId = Guid.NewGuid(),
            OccurredOnUtc = now
        };
        var olderEvent = new WorkflowCreatedEvent(workflowId, correlationId, "{}")
        {
            EventId = Guid.NewGuid(),
            OccurredOnUtc = now.AddMinutes(-1)
        };
        const string consumerName = "integration-test-inbox-order";

        await using (var context = _fixture.CreateDbContext())
        {
            (await new InboxMessageProcessor(context).ProcessAsync(
                consumerName, "blackbox.workflow.events:0:71", JsonSerializer.Serialize(newerEvent)))
                .Should().Be(InboxProcessingResult.Processed);
        }

        await using (var context = _fixture.CreateDbContext())
        {
            (await new InboxMessageProcessor(context).ProcessAsync(
                consumerName, "blackbox.workflow.events:0:70", JsonSerializer.Serialize(olderEvent)))
                .Should().Be(InboxProcessingResult.Processed);
        }

        await using var verificationContext = _fixture.CreateDbContext();
        var projection = await verificationContext.WorkflowEventProjections.SingleAsync(item => item.WorkflowId == workflowId);
        projection.EventCount.Should().Be(2);
        projection.LastEventId.Should().Be(newerEvent.EventId);
        projection.LastEventType.Should().Be(newerEvent.EventType);
    }

    [DockerFact]
    public async Task InboxProcessor_SameEventIdWithDifferentPayload_IsQuarantinedWithoutRepeatingTheEffect()
    {
        var eventId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var originalEvent = new WorkflowCreatedEvent(workflowId, $"INBOX-CONFLICT-{Guid.NewGuid():N}", "{\"orderId\":\"original\"}")
        {
            EventId = eventId
        };
        var conflictingEvent = originalEvent with { PayloadJson = "{\"orderId\":\"tampered\"}" };
        var originalPayload = JsonSerializer.Serialize(originalEvent);
        var conflictingPayload = JsonSerializer.Serialize(conflictingEvent);
        var consumerName = $"integration-conflict-{Guid.NewGuid():N}";

        await using (var context = _fixture.CreateDbContext())
        {
            var result = await new InboxMessageProcessor(context).ProcessAsync(
                consumerName, "blackbox.workflow.events:0:41", originalPayload);
            result.Should().Be(InboxProcessingResult.Processed);
        }

        await using (var context = _fixture.CreateDbContext())
        {
            var processor = new InboxMessageProcessor(context);
            (await processor.ProcessAsync(consumerName, "blackbox.workflow.events:0:42", conflictingPayload))
                .Should().Be(InboxProcessingResult.Quarantined);
            (await processor.ProcessAsync(consumerName, "blackbox.workflow.events:0:43", conflictingPayload))
                .Should().Be(InboxProcessingResult.Quarantined);
        }

        await using var verificationContext = _fixture.CreateDbContext();
        (await verificationContext.InboxMessages.CountAsync(message =>
            message.ConsumerName == consumerName && message.MessageId == eventId.ToString("D")))
            .Should().Be(1);
        var conflictDeadLetters = await verificationContext.DeadLetterMessages.Where(message =>
            message.Source == "InboxEventConflict" && message.DeduplicationKey == $"inbox-event-conflict:{consumerName}:{eventId:D}")
            .ToListAsync();
        conflictDeadLetters.Should().ContainSingle();
        var conflictDeadLetter = conflictDeadLetters.Single();
        conflictDeadLetter.TransportMessageId.Should().Be("blackbox.workflow.events:0:42");
        (await verificationContext.WorkflowEventProjections.SingleAsync(item => item.WorkflowId == workflowId))
            .EventCount.Should().Be(1);
    }

    [DockerFact]
    public async Task InboxProcessor_InvalidJson_IsQuarantinedAndCanBeCommitted()
    {
        const string malformedPayload = "{not-json";
        const string transportMessageId = "blackbox.workflow.events:0:51";
        var consumerName = $"integration-poison-{Guid.NewGuid():N}";

        await using (var context = _fixture.CreateDbContext())
        {
            var result = await new InboxMessageProcessor(context).ProcessAsync(
                consumerName, transportMessageId, malformedPayload);
            result.Should().Be(InboxProcessingResult.Quarantined);
        }

        await using (var context = _fixture.CreateDbContext())
        {
            var retry = await new InboxMessageProcessor(context).ProcessAsync(
                consumerName, transportMessageId, malformedPayload);
            retry.Should().Be(InboxProcessingResult.Quarantined);
        }

        const string secondTransportMessageId = "blackbox.workflow.events:0:52";
        await using (var context = _fixture.CreateDbContext())
        {
            var distinctRecord = await new InboxMessageProcessor(context).ProcessAsync(
                consumerName, secondTransportMessageId, malformedPayload);
            distinctRecord.Should().Be(InboxProcessingResult.Quarantined);
        }

        await using var verificationContext = _fixture.CreateDbContext();
        (await verificationContext.InboxMessages.CountAsync(message => message.ConsumerName == consumerName)).Should().Be(0);

        var deadLetters = await verificationContext.DeadLetterMessages
            .Where(message => message.Source == "InboxInvalidJson" && message.DeduplicationKey != null)
            .OrderBy(message => message.TransportMessageId)
            .ToListAsync();
        deadLetters.Should().HaveCount(2);
        deadLetters.Select(message => message.TransportMessageId)
            .Should().BeEquivalentTo([transportMessageId, secondTransportMessageId]);
        var deadLetter = deadLetters[0];
        using var payload = JsonDocument.Parse(deadLetter.Payload);
        var rawPayloadBase64 = payload.RootElement.GetProperty("rawPayloadBase64").GetString();
        Encoding.UTF8.GetString(Convert.FromBase64String(rawPayloadBase64!)).Should().Be(malformedPayload);
    }

    [DockerFact]
    public async Task InboxProcessor_DuplicateJsonProperties_AreQuarantinedWithRawPayloadAndTransportId()
    {
        var eventId = Guid.NewGuid();
        var transportMessageId = "blackbox.workflow.events:0:61";
        var payload = $"{{\"EventId\":\"{eventId:D}\",\"EventId\":\"{Guid.NewGuid():D}\",\"WorkflowId\":\"{Guid.NewGuid():D}\",\"EventType\":\"WorkflowCreated.v1\",\"OccurredOnUtc\":\"2026-10-15T00:00:00Z\"}}";
        var consumerName = $"integration-ambiguous-{Guid.NewGuid():N}";

        await using (var context = _fixture.CreateDbContext())
        {
            var result = await new InboxMessageProcessor(context).ProcessAsync(consumerName, transportMessageId, payload);
            result.Should().Be(InboxProcessingResult.Quarantined);
        }

        await using var verificationContext = _fixture.CreateDbContext();
        (await verificationContext.InboxMessages.CountAsync(message => message.ConsumerName == consumerName)).Should().Be(0);
        var deadLetter = await verificationContext.DeadLetterMessages.SingleAsync(message =>
            message.Source == "InboxAmbiguousJson" && message.TransportMessageId == transportMessageId);
        using var storedPayload = JsonDocument.Parse(deadLetter.Payload);
        var rawPayloadBase64 = storedPayload.RootElement.GetProperty("rawPayloadBase64").GetString();
        Encoding.UTF8.GetString(Convert.FromBase64String(rawPayloadBase64!)).Should().Be(payload);
    }

    [DockerFact]
    public async Task ReserveResource_WithPessimisticLock_ShouldEnforceCapacityInvariant()
    {

        // Arrange
        await using var dbContext = _fixture.CreateDbContext();
        var resourceRepo = new ResourceRepository(dbContext);
        var orchestrator = new WorkflowOrchestrator(dbContext, resourceRepo, NullLogger<WorkflowOrchestrator>.Instance);

        var resourceId = $"SKU-TEST-CAP-{Guid.NewGuid():N}";
        var resource = new Resource(resourceId, "Limited Test Asset", 1);
        await dbContext.Resources.AddAsync(resource);
        await dbContext.SaveChangesAsync();

        var workflowA = await orchestrator.StartWorkflowAsync($"CORR-WFA-{Guid.NewGuid():N}", "{}");
        var workflowB = await orchestrator.StartWorkflowAsync($"CORR-WFB-{Guid.NewGuid():N}", "{}");

        // Act 1: Workflow A reserves the 1 available unit
        var reservationA = await orchestrator.ReserveResourceAsync(workflowA.Id, resourceId, 1);
        reservationA.Should().NotBeNull();
        reservationA.Status.Should().Be(ReservationStatus.Active);

        // Act 2: Workflow B attempts to reserve from exhausted capacity
        Func<Task> actB = async () => await orchestrator.ReserveResourceAsync(workflowB.Id, resourceId, 1);

        // Assert
        await actB.Should().ThrowAsync<WorkflowInvariantException>()
            .WithMessage($"*InsufficientResourceCapacity*Cannot reserve 1 units of '{resourceId}'*");

        // Verify database state: Available must remain 0, Allocated 1
        var freshResource = await dbContext.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId);
        freshResource!.AvailableQuantity.Should().Be(0);
        freshResource.AllocatedQuantity.Should().Be(1);
    }
}
