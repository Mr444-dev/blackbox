using System.Text.Json;
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
                .RecordIfNewAsync(consumerName, firstDeliveryId, payload);
            firstDelivery.Should().BeTrue();
        }

        await using (var restartedContext = _fixture.CreateDbContext())
        {
            var replayedDelivery = await new InboxMessageProcessor(restartedContext)
                .RecordIfNewAsync(consumerName, redeliveryId, payload);
            replayedDelivery.Should().BeFalse();

            var receiptCount = await restartedContext.InboxMessages.CountAsync(message =>
                message.ConsumerName == consumerName &&
                message.MessageId == eventId.ToString("D"));
            receiptCount.Should().Be(1);
        }
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
