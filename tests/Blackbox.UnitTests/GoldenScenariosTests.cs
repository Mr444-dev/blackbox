using Blackbox.Core.Verification;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class GoldenScenariosTests
{
    private readonly GoldenScenarioRunner _runner = new();

    [Theory]
    [InlineData("duplicate-request.json")]
    [InlineData("provider-timeout.json")]
    [InlineData("out-of-order-events.json")]
    [InlineData("db-deadlock.json")]
    [InlineData("schema-breaking-change.json")]
    [InlineData("retry-storm.json")]
    public async Task RunGoldenScenario_FromDisk_ProducesSuccessfulGuaranteeReport(string filename)
    {
        // Arrange
        var scenarioPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scenarios", filename);
        var jsonContent = await File.ReadAllTextAsync(scenarioPath);

        // Act
        var result = await _runner.RunScenarioJsonAsync(jsonContent);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue($"Scenario {result.ScenarioId} ({result.Title}) must pass expected state and guarantee checks");
        result.ActualState.Should().Be(result.ExpectedState);
        result.GuaranteeReport.OverallPassed.Should().BeTrue();
        result.GuaranteeReport.TotalViolations.Should().Be(0);
        result.GuaranteeReport.SignatureSummary.Should().StartWith("PASS:");
    }

    [Fact]
    public async Task DeterministicReplay_Incident1842_ReproducesAccuratelyWithInvariantsPreserved()
    {
        // Arrange
        var incident = new IncidentSnapshot(
            IncidentId: "INC-1842",
            Timestamp: DateTime.UtcNow.AddHours(-3),
            WorkflowId: Guid.NewGuid().ToString(),
            CorrelationId: "corr-inc-1842",
            InitialPayload: "{\"orderId\":\"ORD-8819\",\"amount\":350.00}",
            InjectedFaultType: "TCP_CONNECTION_RESET",
            ErrorMessage: "Downstream connection reset by peer during carrier confirmation",
            HistoricalEventLog: new List<string> { "WorkflowCreated", "WorkflowValidated", "ProviderTimeout", "WorkflowFailed" }
        );

        // Act
        var replayResult = await _runner.ReplayIncidentAsync(incident);

        // Assert
        replayResult.Should().NotBeNull();
        replayResult.IncidentId.Should().Be("INC-1842");
        replayResult.Reproducible.Should().BeTrue();
        replayResult.GuaranteeReport.OverallPassed.Should().BeTrue();
        replayResult.GuaranteeReport.SignatureSummary.Should().StartWith("PASS:");
        replayResult.ReplaySummary.Should().Contain("successfully reproduced deterministically");
    }
}
