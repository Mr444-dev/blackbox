using Blackbox.Core.Domain;
using Blackbox.Infrastructure.External;
using Blackbox.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blackbox.UnitTests;

public class CarrierResilienceTests
{
    private BlackboxDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<BlackboxDbContext>()
            .UseInMemoryDatabase(databaseName: $"Blackbox_Resilience_{Guid.NewGuid()}")
            .Options;

        return new BlackboxDbContext(options);
    }

    [Fact]
    public async Task DispatchShipment_HealthyMode_ShouldSucceedOnFirstAttempt()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        var workflow = WorkflowInstance.Create("CORR-HEALTHY", "{}");
        workflow.Validate();
        workflow.Reserve();
        await dbContext.Workflows.AddAsync(workflow);
        await dbContext.SaveChangesAsync();

        var service = new ResilientCarrierService(dbContext, NullLogger<ResilientCarrierService>.Instance);

        // Act
        var result = await service.DispatchShipmentAsync(workflow.Id, "123 Main St", CarrierSimulatorMode.Healthy);

        // Assert
        result.Success.Should().BeTrue();
        result.TrackingNumber.Should().StartWith("TRK-");
        result.AttemptCount.Should().Be(1);

        workflow.Status.Should().Be(WorkflowStatus.WaitingCallback);
    }

    [Fact]
    public async Task DispatchShipment_FailTwiceThenSucceed_PollyShouldRetryAndRecover()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        var workflow = WorkflowInstance.Create("CORR-RETRY-RECOVER", "{}");
        workflow.Validate();
        workflow.Reserve();
        await dbContext.Workflows.AddAsync(workflow);
        await dbContext.SaveChangesAsync();

        var service = new ResilientCarrierService(dbContext, NullLogger<ResilientCarrierService>.Instance);

        // Act
        var result = await service.DispatchShipmentAsync(
            workflow.Id, 
            "456 High St", 
            CarrierSimulatorMode.FailTwiceThenSucceed);

        // Assert: Polly automatically retried and succeeded on attempt 3
        result.Success.Should().BeTrue();
        result.TrackingNumber.Should().StartWith("TRK-");
        result.AttemptCount.Should().Be(3);

        workflow.Status.Should().Be(WorkflowStatus.WaitingCallback);
    }

    [Fact]
    public async Task DispatchShipment_AlwaysFail500_ShouldQuarantineToDeadLetterQueue()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        var workflow = WorkflowInstance.Create("CORR-FAIL-DLQ", "{}");
        workflow.Validate();
        workflow.Reserve();
        await dbContext.Workflows.AddAsync(workflow);
        await dbContext.SaveChangesAsync();

        var service = new ResilientCarrierService(dbContext, NullLogger<ResilientCarrierService>.Instance);

        // Act
        var result = await service.DispatchShipmentAsync(
            workflow.Id, 
            "789 Error Ave", 
            CarrierSimulatorMode.AlwaysFail500);

        // Assert
        result.Success.Should().BeFalse();
        result.AttemptCount.Should().BeGreaterThan(1);

        // Invariant: Poisoned / failed external request must be quarantined in DLQ
        var dlqItem = await dbContext.DeadLetterMessages.FirstOrDefaultAsync(d => d.WorkflowId == workflow.Id);
        dlqItem.Should().NotBeNull();
        dlqItem!.Source.Should().Be("CarrierDispatch");
        dlqItem.Status.Should().Be(Core.Outbox.DeadLetterStatus.Quarantined);
        dlqItem.ExceptionMessage.Should().MatchRegex("(Circuit Breaker Tripped|Carrier Dispatch Exhausted)");
    }
}
