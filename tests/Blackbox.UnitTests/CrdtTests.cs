using Blackbox.Core.Crdt;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class CrdtTests
{
    [Fact]
    public void VectorClock_ConcurrentUpdates_ShouldDetectConcurrency()
    {
        // Arrange
        var clockRegionA = new VectorClock();
        var clockRegionB = new VectorClock();

        // Region A increments its clock
        clockRegionA.Increment("westeurope");

        // Region B increments its clock independently
        clockRegionB.Increment("polandcentral");

        // Act & Assert
        clockRegionA.CompareTo(clockRegionB).Should().Be(ClockComparison.Concurrent);
        clockRegionB.CompareTo(clockRegionA).Should().Be(ClockComparison.Concurrent);

        // Merge clocks
        var merged = clockRegionA.Merge(clockRegionB);
        merged.CompareTo(clockRegionA).Should().Be(ClockComparison.After);
        merged.CompareTo(clockRegionB).Should().Be(ClockComparison.After);
    }

    [Fact]
    public void PnCounter_ConcurrentCrossRegionOperations_ShouldConvergeDeterministically()
    {
        // Arrange: Region A (westeurope) and Region B (polandcentral)
        var regionACounter = new PnCounter();
        var regionBCounter = new PnCounter();

        // Region A allocates 10 units, releases 2 units
        regionACounter.Increment("westeurope", 10);
        regionACounter.Decrement("westeurope", 2);

        // Region B allocates 5 units, releases 1 unit
        regionBCounter.Increment("polandcentral", 5);
        regionBCounter.Decrement("polandcentral", 1);

        // Act: Bidirectional cross-region gossip/replication merge
        var mergedAtA = regionACounter.Merge(regionBCounter);
        var mergedAtB = regionBCounter.Merge(regionACounter);

        // Assert:
        // Expected value: (10 - 2) + (5 - 1) = 12
        mergedAtA.Value.Should().Be(12);
        mergedAtB.Value.Should().Be(12);

        // Commutative Invariant: Merge order does not alter outcome
        mergedAtA.Value.Should().Be(mergedAtB.Value);

        // Idempotent Invariant: Merging with self causes no state changes
        mergedAtA.Merge(mergedAtA).Value.Should().Be(12);
    }

    [Fact]
    public void LwwRegister_ConcurrentWrites_ShouldDeterministicallyResolveToLatestTimestamp()
    {
        // Arrange: Base time
        var now = DateTime.UtcNow.Ticks;
        var regA = new LwwRegister<string>("OrderPlaced", "westeurope", now + 100);
        var regB = new LwwRegister<string>("OrderCancelled", "polandcentral", now + 200); // Newer timestamp

        // Act
        var merged = regA.Merge(regB);

        // Assert: Newer timestamp from Region B wins
        merged.Value.Should().Be("OrderCancelled");
        merged.NodeId.Should().Be("polandcentral");

        // Verify reverse merge yields identical deterministic result
        var reverseMerged = regB.Merge(regA);
        reverseMerged.Value.Should().Be("OrderCancelled");
    }

    [Fact]
    public void LwwRegister_IdenticalTimestamp_ShouldTieBreakUsingNodeIdDeterministically()
    {
        // Arrange: Exactly identical timestamp ticks
        var identicalTime = DateTime.UtcNow.Ticks;
        var regA = new LwwRegister<string>("StateA", "node-1", identicalTime);
        var regB = new LwwRegister<string>("StateB", "node-2", identicalTime);

        // Act
        var merged = regA.Merge(regB);

        // Assert: "node-2" is lexicographically greater than "node-1"
        merged.NodeId.Should().Be("node-2");
        merged.Value.Should().Be("StateB");
    }
}
