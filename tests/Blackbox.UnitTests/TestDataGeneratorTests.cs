using Blackbox.Core.Verification;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class TestDataGeneratorTests
{
    [Fact]
    public void GenerateWorkflows_GeneratesAccurateControlledAnomalyDistribution()
    {
        // Arrange
        var generator = new TestDataGenerator(seed: 999);
        var config = new TestDataDistributionConfig
        {
            TotalWorkflows = 10000,
            DuplicateRequestPercentage = 1.0, // 1%
            DelayedCallbackPercentage = 0.5,  // 0.5%
            MalformedPayloadPercentage = 0.1, // 0.1%
            FailedProviderPercentage = 2.0    // 2%
        };

        // Act
        var batch = generator.GenerateWorkflows(config).ToList();
        var summary = generator.SummarizeBatch(batch);

        // Assert
        summary.TotalGenerated.Should().Be(10000);
        summary.TotalEventsSimulated.Should().BeGreaterThan(40000);

        // Verify approximate distributions (within statistical tolerance for 10k samples)
        // 1% duplicates ~= 100
        summary.DuplicateAnomalies.Should().BeInRange(50, 160);

        // 0.5% delayed ~= 50
        summary.DelayedAnomalies.Should().BeInRange(20, 90);

        // 0.1% malformed ~= 10
        summary.MalformedAnomalies.Should().BeInRange(2, 30);

        // 2% failed providers ~= 200
        summary.FailedProviderAnomalies.Should().BeInRange(130, 280);

        // Bulk of traffic is normal successful workflows
        summary.NormalWorkflows.Should().BeGreaterThan(9000);
    }
}
