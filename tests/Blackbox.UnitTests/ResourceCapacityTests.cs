using Blackbox.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class ResourceCapacityTests
{
    [Fact]
    public void Reserve_WithSufficientCapacity_ShouldDeductAvailableAndIncreaseAllocated()
    {
        // Arrange
        var resource = new Resource("SKU-LAPTOP-001", "High-End Laptop", 5);
        var workflowId = Guid.NewGuid();

        // Act
        var reservation = resource.Reserve(workflowId, 2);

        // Assert
        reservation.Should().NotBeNull();
        reservation.Quantity.Should().Be(2);
        reservation.Status.Should().Be(ReservationStatus.Active);
        resource.AvailableQuantity.Should().Be(3);
        resource.AllocatedQuantity.Should().Be(2);
        resource.Version.Should().Be(2);
    }

    [Fact]
    public void Reserve_ExceedingCapacity_ShouldThrowInvariantException()
    {
        // Arrange
        var resource = new Resource("SKU-LIMITED-001", "Limited Resource", 1);
        var workflowId = Guid.NewGuid();

        // Act
        Action act = () => resource.Reserve(workflowId, 2);

        // Assert
        act.Should().Throw<WorkflowInvariantException>()
           .WithMessage("*InsufficientResourceCapacity*Cannot reserve 2 units of 'SKU-LIMITED-001'*");
        resource.AvailableQuantity.Should().Be(1);
        resource.AllocatedQuantity.Should().Be(0);
    }

    [Fact]
    public void ReleaseReservation_ShouldRestoreAvailableQuantity()
    {
        // Arrange
        var resource = new Resource("SKU-001", "Stock Item", 10);
        var reservation = resource.Reserve(Guid.NewGuid(), 4);

        // Act
        resource.ReleaseReservation(reservation);

        // Assert
        reservation.Status.Should().Be(ReservationStatus.Released);
        resource.AvailableQuantity.Should().Be(10);
        resource.AllocatedQuantity.Should().Be(0);
    }
}
