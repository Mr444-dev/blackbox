using Blackbox.Core.Verification;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class PropertyBasedWorkflowTests
{
    [Fact]
    public void PropertyBased_FuzzSequence_PreservesAllFormalInvariantsUnderRandomizedOperations()
    {
        // Arrange
        var engine = new PropertyBasedWorkflowEngine(seed: 1337);

        // Act: Run 200 randomized command sequences (3,000 operations, 12,000 invariant checks)
        var result = engine.RunPropertyBasedInvariantSuite(sequenceCount: 200, commandsPerSequence: 15);

        // Assert
        result.Should().NotBeNull();
        result.AllInvariantsPreserved.Should().BeTrue();
        result.TotalSequencesRun.Should().Be(200);
        result.TotalCommandsExecuted.Should().Be(3000);
        result.InvariantChecksPassed.Should().Be(12000);
        result.LegalTransitionsCount.Should().BeGreaterThan(0);
        result.BlockedIllegalTransitionsCount.Should().BeGreaterThan(0);
        result.InvariantsVerified.Should().HaveCount(5);
    }
}
