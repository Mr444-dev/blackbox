using Blackbox.Core.Domain;
using Blackbox.Core.Saga;
using Blackbox.Infrastructure.External;
using Blackbox.Infrastructure.Persistence;
using Blackbox.Infrastructure.Saga;
using Blackbox.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blackbox.UnitTests;

public class SagaOrchestratorTests
{
    private BlackboxDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<BlackboxDbContext>()
            .UseInMemoryDatabase(databaseName: $"Blackbox_Saga_{Guid.NewGuid()}")
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new BlackboxDbContext(options);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAllStepsSucceed_ShouldReturnCompletedSaga()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        var resource = new Resource("SKU-SAGA-01", "Saga Laptop", 5);
        await dbContext.Resources.AddAsync(resource);
        await dbContext.SaveChangesAsync();

        var resourceRepo = new ResourceRepository(dbContext);
        var workflowOrchestrator = new WorkflowOrchestrator(dbContext, resourceRepo, NullLogger<WorkflowOrchestrator>.Instance);
        var workflow = await workflowOrchestrator.StartWorkflowAsync("CORR-SAGA-OK", "{}");

        var carrierService = new ResilientCarrierService(dbContext, NullLogger<ResilientCarrierService>.Instance);

        var saga = new SagaOrchestrator<FulfillmentSagaContext>(NullLogger.Instance)
            .AddStep(new ReserveInventorySagaStep(workflowOrchestrator, dbContext, NullLogger<ReserveInventorySagaStep>.Instance))
            .AddStep(new DispatchCarrierSagaStep(carrierService, NullLogger<DispatchCarrierSagaStep>.Instance))
            .AddStep(new ProcessPaymentSagaStep(NullLogger<ProcessPaymentSagaStep>.Instance));

        var context = new FulfillmentSagaContext
        {
            WorkflowId = workflow.Id,
            CorrelationId = workflow.CorrelationId,
            ResourceId = "SKU-SAGA-01",
            Quantity = 2,
            DestinationAddress = "Marszalkowska 1, Warszawa",
            Amount = 4500.00m,
            SimulatePaymentFailure = false
        };

        // Act
        var result = await saga.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(SagaExecutionStatus.Completed);
        result.ExecutedSteps.Should().ContainInOrder("ReserveInventory", "DispatchCarrier", "ProcessPayment");
        result.CompensatedSteps.Should().BeEmpty();

        context.Reservation.Should().NotBeNull();
        context.TrackingNumber.Should().NotBeNull();
        context.PaymentCaptured.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPaymentFails_ShouldRollbackAndCompensateInReverseOrder()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        var resource = new Resource("SKU-SAGA-ROLLBACK", "Limited Resource", 1);
        await dbContext.Resources.AddAsync(resource);
        await dbContext.SaveChangesAsync();

        var resourceRepo = new ResourceRepository(dbContext);
        var workflowOrchestrator = new WorkflowOrchestrator(dbContext, resourceRepo, NullLogger<WorkflowOrchestrator>.Instance);
        var workflow = await workflowOrchestrator.StartWorkflowAsync("CORR-SAGA-FAIL", "{}");

        var carrierService = new ResilientCarrierService(dbContext, NullLogger<ResilientCarrierService>.Instance);

        var saga = new SagaOrchestrator<FulfillmentSagaContext>(NullLogger.Instance)
            .AddStep(new ReserveInventorySagaStep(workflowOrchestrator, dbContext, NullLogger<ReserveInventorySagaStep>.Instance))
            .AddStep(new DispatchCarrierSagaStep(carrierService, NullLogger<DispatchCarrierSagaStep>.Instance))
            .AddStep(new ProcessPaymentSagaStep(NullLogger<ProcessPaymentSagaStep>.Instance));

        var context = new FulfillmentSagaContext
        {
            WorkflowId = workflow.Id,
            CorrelationId = workflow.CorrelationId,
            ResourceId = "SKU-SAGA-ROLLBACK",
            Quantity = 1,
            DestinationAddress = "Krakowskie Przedmiescie 10, Warszawa",
            Amount = 1200.00m,
            SimulatePaymentFailure = true // Trigger failure in step 3!
        };

        // Act
        var result = await saga.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(SagaExecutionStatus.Compensated);
        result.FailedStep.Should().Be("ProcessPayment");

        // Verification of reverse compensation order: DispatchCarrier was compensated first, then ReserveInventory!
        result.CompensatedSteps.Should().ContainInOrder("DispatchCarrier", "ReserveInventory");

        // Verification of database state:
        // Because ReserveInventory was compensated, the resource capacity MUST BE RESTORED back to 1!
        var updatedResource = await dbContext.Resources.FirstOrDefaultAsync(r => r.Id == "SKU-SAGA-ROLLBACK");
        updatedResource!.AvailableQuantity.Should().Be(1);
        updatedResource.AllocatedQuantity.Should().Be(0);
    }
}
