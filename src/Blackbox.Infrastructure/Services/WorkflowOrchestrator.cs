using System.Text.Json;
using Blackbox.Core.Domain;
using Blackbox.Core.Domain.Events;
using Blackbox.Core.Outbox;
using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure.Services;

public interface IWorkflowOrchestrator
{
    Task<WorkflowInstance> StartWorkflowAsync(string correlationId, string payloadJson, CancellationToken ct = default);
    Task<ResourceReservation> ReserveResourceAsync(Guid workflowId, string resourceId, int quantity, CancellationToken ct = default);
    Task<WorkflowInstance> CompleteWorkflowAsync(Guid workflowId, CancellationToken ct = default);
    Task<WorkflowInstance> FailWorkflowAsync(Guid workflowId, string reason, CancellationToken ct = default);
}

public class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private readonly BlackboxDbContext _dbContext;
    private readonly IResourceRepository _resourceRepository;
    private readonly ILogger<WorkflowOrchestrator> _logger;

    public WorkflowOrchestrator(
        BlackboxDbContext dbContext,
        IResourceRepository resourceRepository,
        ILogger<WorkflowOrchestrator> logger)
    {
        _dbContext = dbContext;
        _resourceRepository = resourceRepository;
        _logger = logger;
    }

    public async Task<WorkflowInstance> StartWorkflowAsync(string correlationId, string payloadJson, CancellationToken ct = default)
    {
        // 1. Idempotency Check: if this correlation ID was already processed, return existing workflow
        var existing = await _dbContext.Workflows
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.CorrelationId == correlationId, ct);

        if (existing != null)
        {
            _logger.LogInformation("Idempotent hit: Workflow with correlationId {CorrelationId} already exists (Status: {Status}).", 
                correlationId, existing.Status);
            return existing;
        }

        // 2. Begin atomic transaction (Workflow creation + Outbox message)
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var workflow = WorkflowInstance.Create(correlationId, payloadJson);
            await _dbContext.Workflows.AddAsync(workflow, ct);

            // Add domain events to Transactional Outbox
            foreach (var domainEvent in workflow.DomainEvents)
            {
                var outboxMsg = OutboxMessage.Create(
                    topic: "blackbox.workflow.events",
                    messageKey: workflow.Id.ToString(),
                    eventType: domainEvent.EventType,
                    payload: JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
                );
                await _dbContext.OutboxMessages.AddAsync(outboxMsg, ct);
            }

            workflow.ClearDomainEvents();
            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation("Workflow {WorkflowId} successfully created with correlationId {CorrelationId}.", 
                workflow.Id, correlationId);
            return workflow;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Transaction rolled back during workflow start for correlationId {CorrelationId}.", correlationId);
            throw;
        }
    }

    public async Task<ResourceReservation> ReserveResourceAsync(Guid workflowId, string resourceId, int quantity, CancellationToken ct = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var workflow = await _dbContext.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId, ct);
            if (workflow == null)
                throw new InvalidOperationException($"Workflow {workflowId} does not exist.");

            // 1. Advance workflow state machine to Validated if needed, then Reserved
            if (workflow.Status == WorkflowStatus.Created)
            {
                workflow.Validate();
            }

            // 2. Lock resource with SELECT ... FOR UPDATE via Dapper
            var resource = await _resourceRepository.GetByIdForUpdateAsync(resourceId, ct);
            if (resource == null)
                throw new InvalidOperationException($"Resource '{resourceId}' does not exist in inventory.");

            // 3. Apply business capacity invariant
            var reservation = resource.Reserve(workflowId, quantity);
            workflow.Reserve();

            // 4. Persist resource updates & reservation
            await _resourceRepository.UpdateAsync(resource, ct);
            await _resourceRepository.AddReservationAsync(reservation, ct);

            // 5. Add ResourceReserved event to Outbox
            var outboxMsg = OutboxMessage.Create(
                topic: "blackbox.resource.events",
                messageKey: resourceId,
                eventType: "ResourceReserved.v1",
                payload: JsonSerializer.Serialize(new ResourceReservedEvent(workflowId, resourceId, quantity))
            );
            await _dbContext.OutboxMessages.AddAsync(outboxMsg, ct);

            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation("Reserved {Quantity} units of {ResourceId} for workflow {WorkflowId}.", 
                quantity, resourceId, workflowId);
            return reservation;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogWarning(ex, "Reservation failed for workflow {WorkflowId} and resource {ResourceId}.", workflowId, resourceId);
            throw;
        }
    }

    public async Task<WorkflowInstance> CompleteWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var workflow = await _dbContext.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId, ct);
            if (workflow == null)
                throw new InvalidOperationException($"Workflow {workflowId} does not exist.");

            if (workflow.Status == WorkflowStatus.Reserved)
            {
                workflow.StartProcessing();
            }

            workflow.Complete();

            foreach (var domainEvent in workflow.DomainEvents)
            {
                var outboxMsg = OutboxMessage.Create(
                    topic: "blackbox.workflow.events",
                    messageKey: workflow.Id.ToString(),
                    eventType: domainEvent.EventType,
                    payload: JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
                );
                await _dbContext.OutboxMessages.AddAsync(outboxMsg, ct);
            }

            workflow.ClearDomainEvents();
            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return workflow;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Transaction rolled back during workflow completion for workflow {WorkflowId}.", workflowId);
            throw;
        }
    }

    public async Task<WorkflowInstance> FailWorkflowAsync(Guid workflowId, string reason, CancellationToken ct = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var workflow = await _dbContext.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId, ct);
            if (workflow == null)
                throw new InvalidOperationException($"Workflow {workflowId} does not exist.");

            workflow.Fail(reason);

            foreach (var domainEvent in workflow.DomainEvents)
            {
                var outboxMsg = OutboxMessage.Create(
                    topic: "blackbox.workflow.events",
                    messageKey: workflow.Id.ToString(),
                    eventType: domainEvent.EventType,
                    payload: JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
                );
                await _dbContext.OutboxMessages.AddAsync(outboxMsg, ct);
            }

            workflow.ClearDomainEvents();
            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return workflow;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Transaction rolled back during workflow failure for workflow {WorkflowId}.", workflowId);
            throw;
        }
    }
}
