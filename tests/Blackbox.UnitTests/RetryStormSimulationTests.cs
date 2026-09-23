using Blackbox.Infrastructure.External;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class RetryStormSimulationTests
{
    [Fact]
    public async Task RunRetryStormSimulation_Under1000Requests_PreventsSystemMeltdownViaCircuitBreakerAndRetryBudget()
    {
        // Arrange
        var simulator = new RetryStormSimulator();
        var config = new RetryStormConfig
        {
            TotalInboundRequests = 1000,
            CircuitBreakerFailureThreshold = 50,
            CircuitOpenDuration = TimeSpan.FromMilliseconds(500),
            MaxRetriesPerRequest = 3,
            BaseBackoffMs = 5,
            MaxBackoffMs = 50,
            GlobalRetryBudget = 20 // Strict token bucket
        };

        // Act: Fire 1,000 requests into DOWN provider
        var result = await simulator.RunRetryStormSimulationAsync(config, providerAlwaysDown: true);

        // Assert
        result.Should().NotBeNull();
        result.TotalInboundRequests.Should().Be(1000);

        // Circuit breaker must trip and fast-fail the majority of requests without network calls
        result.CircuitBreakerFastFails.Should().BeGreaterThan(500);

        // Retry budget must actively suppress retries (load shedding)
        result.RetriesSuppressedByBudget.Should().BeGreaterThan(0);

        // Total external calls actually executed must be a small fraction of potential 4,000 calls
        result.ExternalCallsExecuted.Should().BeLessThan(400);

        // All failed requests must be cleanly quarantined to DLQ rather than hanging
        result.QuarantinedToDlq.Should().Be(1000);
        result.SelfHealingDemonstrated.Should().BeTrue();
    }
}
