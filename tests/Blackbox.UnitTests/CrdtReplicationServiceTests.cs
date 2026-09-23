using Blackbox.Core.Crdt;
using Blackbox.Infrastructure.Crdt;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class CrdtReplicationServiceTests
{
    private readonly CrdtReplicationService _service = new();

    [Fact]
    public void MutateCounter_IncrementsAndDecrementsAcrossRegions_ShouldCalculateCorrectConvergedValue()
    {
        // Act: West Europe increments 20 and decrements 5
        _service.MutateCounter("sku-cloud-vm", "westeurope", 20);
        _service.MutateCounter("sku-cloud-vm", "westeurope", -5);

        // Act: Poland Central increments 15 and decrements 2
        _service.MutateCounter("sku-cloud-vm", "polandcentral", 15);
        _service.MutateCounter("sku-cloud-vm", "polandcentral", -2);

        // Assert
        var counter = _service.GetCounter("sku-cloud-vm");
        counter.Value.Should().Be(28); // (20 - 5) + (15 - 2) = 15 + 13 = 28
        counter.P["westeurope"].Should().Be(20);
        counter.N["westeurope"].Should().Be(5);
        counter.P["polandcentral"].Should().Be(15);
        counter.N["polandcentral"].Should().Be(2);
    }

    [Fact]
    public void MergeCounter_WithRemoteGossipState_ShouldDeterministicallyConverge()
    {
        // Arrange
        var resourceId = "global-inventory-item";
        _service.MutateCounter(resourceId, "westeurope", 100);

        // Remote peer has different regional increments
        var remoteCounter = new PnCounter();
        remoteCounter.Increment("polandcentral", 50);
        remoteCounter.Decrement("polandcentral", 10);
        remoteCounter.Increment("eastus", 30);

        // Act
        var merged = _service.MergeCounter(resourceId, remoteCounter);

        // Assert: 100 + (50 - 10) + 30 = 170
        merged.Value.Should().Be(170);
        _service.GetCounter(resourceId).Value.Should().Be(170);
    }

    [Fact]
    public void LwwRegister_ConcurrentMutations_ShouldDeterministicallyTieBreakOrPickLatest()
    {
        var resourceId = "order-status-reg";
        var baseTicks = DateTime.UtcNow.Ticks;

        // Node A sets state at baseTicks + 10
        _service.MutateRegister(resourceId, "APPROVED", "node-a", baseTicks + 10);

        // Node B sets state at baseTicks + 50 (strictly newer)
        _service.MutateRegister(resourceId, "SHIPPED", "node-b", baseTicks + 50);

        // Stale write from Node C with baseTicks + 20 arrives late
        _service.MutateRegister(resourceId, "CANCELLED", "node-c", baseTicks + 20);

        var register = _service.GetRegister(resourceId);
        register.Should().NotBeNull();
        register!.Value.Should().Be("SHIPPED");
        register.NodeId.Should().Be("node-b");
    }
}
