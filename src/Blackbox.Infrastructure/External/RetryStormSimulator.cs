namespace Blackbox.Infrastructure.External;

public record RetryStormConfig
{
    public int TotalInboundRequests { get; init; } = 1000;
    public int CircuitBreakerFailureThreshold { get; init; } = 5;
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaxRetriesPerRequest { get; init; } = 3;
    public int BaseBackoffMs { get; init; } = 20;
    public int MaxBackoffMs { get; init; } = 300;
    public int GlobalRetryBudget { get; init; } = 100; // Ceiling on total retry attempts across fleet
}

public record RetryStormSimulationResult(
    int TotalInboundRequests,
    int CircuitBreakerFastFails,
    int ExternalCallsExecuted,
    int RetriesSuppressedByBudget,
    int SuccessfulWorkflows,
    int QuarantinedToDlq,
    bool SelfHealingDemonstrated);

public class RetryStormSimulator
{
    private readonly Random _random = new(42);

    public async Task<RetryStormSimulationResult> RunRetryStormSimulationAsync(
        RetryStormConfig config,
        bool providerAlwaysDown = true,
        CancellationToken cancellationToken = default)
    {
        int circuitFastFails = 0;
        int externalCallsExecuted = 0;
        int retriesSuppressed = 0;
        int quarantinedToDlq = 0;
        int successful = 0;

        int consecutiveFailures = 0;
        bool isCircuitOpen = false;
        DateTime circuitOpenedAt = DateTime.MinValue;

        int remainingRetryBudget = config.GlobalRetryBudget;
        var lockObj = new object();

        // Concurrently simulate 1,000 requests hitting the failing provider
        var tasks = Enumerable.Range(0, config.TotalInboundRequests).Select(async i =>
        {
            await Task.Yield();

            bool requestResolved = false;
            int attempt = 0;

            while (!requestResolved && attempt <= config.MaxRetriesPerRequest)
            {
                attempt++;

                // Check Circuit Breaker State
                lock (lockObj)
                {
                    if (isCircuitOpen)
                    {
                        if (DateTime.UtcNow - circuitOpenedAt > config.CircuitOpenDuration)
                        {
                            // Half-open attempt
                            isCircuitOpen = false;
                            consecutiveFailures = 0;
                        }
                        else
                        {
                            Interlocked.Increment(ref circuitFastFails);
                            break; // Fast fail immediately without calling provider
                        }
                    }
                }

                // Check Retry Budget if this is a retry (attempt > 1)
                if (attempt > 1)
                {
                    lock (lockObj)
                    {
                        if (remainingRetryBudget <= 0)
                        {
                            Interlocked.Increment(ref retriesSuppressed); // Load shedding!
                            break;
                        }
                        remainingRetryBudget--;
                    }

                    // Exponential Backoff with Full Jitter
                    int exponential = (int)(config.BaseBackoffMs * Math.Pow(2, attempt - 1));
                    int maxCap = Math.Min(config.MaxBackoffMs, exponential);
                    int jitteredSleepMs;
                    lock (_random)
                    {
                        jitteredSleepMs = _random.Next(0, maxCap + 1);
                    }
                    await Task.Delay(jitteredSleepMs, cancellationToken);
                }

                // Execute external call
                Interlocked.Increment(ref externalCallsExecuted);

                if (providerAlwaysDown)
                {
                    // Simulated 503 Service Unavailable
                    lock (lockObj)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= config.CircuitBreakerFailureThreshold)
                        {
                            isCircuitOpen = true;
                            circuitOpenedAt = DateTime.UtcNow;
                        }
                    }
                }
                else
                {
                    requestResolved = true;
                    Interlocked.Increment(ref successful);
                    lock (lockObj)
                    {
                        consecutiveFailures = 0;
                    }
                }
            }

            if (!requestResolved)
            {
                Interlocked.Increment(ref quarantinedToDlq);
            }
        });

        await Task.WhenAll(tasks);

        return new RetryStormSimulationResult(
            TotalInboundRequests: config.TotalInboundRequests,
            CircuitBreakerFastFails: circuitFastFails,
            ExternalCallsExecuted: externalCallsExecuted,
            RetriesSuppressedByBudget: retriesSuppressed,
            SuccessfulWorkflows: successful,
            QuarantinedToDlq: quarantinedToDlq,
            SelfHealingDemonstrated: circuitFastFails > 0 && retriesSuppressed > 0);
    }
}
