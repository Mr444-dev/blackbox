using Blackbox.Core.Domain;
using Blackbox.Core.Saga;
using Blackbox.Infrastructure.External;
using Blackbox.Infrastructure.Persistence;
using Blackbox.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure.Saga;

public class FulfillmentSagaContext
{
    public Guid WorkflowId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string DestinationAddress { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool SimulatePaymentFailure { get; set; }

    // State captured during saga progress
    public ResourceReservation? Reservation { get; set; }
    public string? TrackingNumber { get; set; }
    public bool PaymentCaptured { get; set; }
}

public class ReserveInventorySagaStep : ISagaStep<FulfillmentSagaContext>
{
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly BlackboxDbContext _dbContext;
    private readonly ILogger<ReserveInventorySagaStep> _logger;

    public string StepName => "ReserveInventory";

    public ReserveInventorySagaStep(
        IWorkflowOrchestrator orchestrator, 
        BlackboxDbContext dbContext,
        ILogger<ReserveInventorySagaStep> logger)
    {
        _orchestrator = orchestrator;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(FulfillmentSagaContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Saga Step [ReserveInventory]: Reserving {Qty} of {Resource} for workflow {WorkflowId}",
            context.Quantity, context.ResourceId, context.WorkflowId);

        context.Reservation = await _orchestrator.ReserveResourceAsync(
            context.WorkflowId, 
            context.ResourceId, 
            context.Quantity, 
            cancellationToken);

        return context.Reservation != null;
    }

    public async Task CompensateAsync(FulfillmentSagaContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Saga Compensation [ReserveInventory]: Releasing reservation for workflow {WorkflowId}", context.WorkflowId);

        if (context.Reservation != null)
        {
            var resource = await _dbContext.Resources.FirstOrDefaultAsync(r => r.Id == context.ResourceId, cancellationToken);
            var reservation = await _dbContext.ResourceReservations.FirstOrDefaultAsync(rr => rr.Id == context.Reservation.Id, cancellationToken);

            if (resource != null && reservation != null)
            {
                resource.ReleaseReservation(reservation);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Saga Compensation [ReserveInventory]: Successfully restored {Qty} units of {Resource}.",
                    reservation.Quantity, resource.Id);
            }
        }
    }
}

public class DispatchCarrierSagaStep : ISagaStep<FulfillmentSagaContext>
{
    private readonly ICarrierService _carrierService;
    private readonly ILogger<DispatchCarrierSagaStep> _logger;

    public string StepName => "DispatchCarrier";

    public DispatchCarrierSagaStep(ICarrierService carrierService, ILogger<DispatchCarrierSagaStep> logger)
    {
        _carrierService = carrierService;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(FulfillmentSagaContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Saga Step [DispatchCarrier]: Booking carrier for workflow {WorkflowId}", context.WorkflowId);

        var dispatchResult = await _carrierService.DispatchShipmentAsync(
            context.WorkflowId, 
            context.DestinationAddress, 
            CarrierSimulatorMode.Healthy, 
            cancellationToken);

        if (dispatchResult.Success)
        {
            context.TrackingNumber = dispatchResult.TrackingNumber;
            return true;
        }

        return false;
    }

    public Task CompensateAsync(FulfillmentSagaContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Saga Compensation [DispatchCarrier]: Cancelling carrier booking {Tracking} for workflow {WorkflowId}",
            context.TrackingNumber, context.WorkflowId);

        // Cancel shipment with external logistics API
        context.TrackingNumber = null;
        return Task.CompletedTask;
    }
}

public class ProcessPaymentSagaStep : ISagaStep<FulfillmentSagaContext>
{
    private readonly ILogger<ProcessPaymentSagaStep> _logger;

    public string StepName => "ProcessPayment";

    public ProcessPaymentSagaStep(ILogger<ProcessPaymentSagaStep> logger)
    {
        _logger = logger;
    }

    public Task<bool> ExecuteAsync(FulfillmentSagaContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Saga Step [ProcessPayment]: Authorizing payment of {Amount:C} for workflow {WorkflowId}",
            context.Amount, context.WorkflowId);

        if (context.SimulatePaymentFailure)
        {
            _logger.LogError("Saga Step [ProcessPayment]: Payment rejected by bank gateway (Insufficient funds / fraud score).");
            return Task.FromResult(false);
        }

        context.PaymentCaptured = true;
        return Task.FromResult(true);
    }

    public Task CompensateAsync(FulfillmentSagaContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Saga Compensation [ProcessPayment]: Issuing automated refund of {Amount:C} for workflow {WorkflowId}",
            context.Amount, context.WorkflowId);

        context.PaymentCaptured = false;
        return Task.CompletedTask;
    }
}
