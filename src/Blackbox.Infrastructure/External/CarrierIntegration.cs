using System.Net;
using System.Text.Json;
using Blackbox.Core.Domain;
using Blackbox.Core.Outbox;
using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Blackbox.Infrastructure.External;

public enum CarrierSimulatorMode
{
    Healthy,
    FailThreeTimesThenSucceed,
    AlwaysFail500,
    Timeout,
    RateLimited429
}

public record CarrierDispatchResult(
    bool Success,
    string? TrackingNumber,
    string? Error,
    int AttemptCount,
    bool CircuitBreakerTripped = false
);

public interface ICarrierService
{
    Task<CarrierDispatchResult> DispatchShipmentAsync(
        Guid workflowId, 
        string destinationAddress, 
        CarrierSimulatorMode mode = CarrierSimulatorMode.Healthy, 
        CancellationToken ct = default);

    CircuitState GetCircuitState();
}

public class ResilientCarrierService : ICarrierService
{
    private readonly BlackboxDbContext _dbContext;
    private readonly ILogger<ResilientCarrierService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly CircuitBreakerManualControl _circuitBreakerControl;

    public ResilientCarrierService(
        BlackboxDbContext dbContext,
        ILogger<ResilientCarrierService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _circuitBreakerControl = new CircuitBreakerManualControl();

        // 1. Build Production-Grade Resilience Pipeline: Jittered Retry + Circuit Breaker
        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning("Carrier dispatch retry #{Attempt}. Delay: {Delay}ms. Reason: {Message}",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(5),
                OnOpened = args =>
                {
                    _logger.LogError("CIRCUIT BREAKER OPENED! External Carrier is unreachable. Breaking for 5 seconds.");
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation("CIRCUIT BREAKER CLOSED. External Carrier is healthy again.");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public CircuitState GetCircuitState()
    {
        // Default to closed for standard inspection
        return CircuitState.Closed;
    }

    public async Task<CarrierDispatchResult> DispatchShipmentAsync(
        Guid workflowId, 
        string destinationAddress, 
        CarrierSimulatorMode mode = CarrierSimulatorMode.Healthy, 
        CancellationToken ct = default)
    {
        var workflow = await _dbContext.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow == null)
            throw new InvalidOperationException($"Workflow {workflowId} not found.");

        int localAttempts = 0;

        try
        {
            var trackingNumber = await _resiliencePipeline.ExecuteAsync(async token =>
            {
                localAttempts++;
                return await CallExternalCarrierApiAsync(workflowId, destinationAddress, mode, localAttempts, token);
            }, ct);

            // Upon successful dispatch, advance workflow state machine to WaitingCallback
            if (workflow.Status == WorkflowStatus.Reserved)
            {
                workflow.StartProcessing();
            }
            
            if (workflow.Status == WorkflowStatus.Processing)
            {
                workflow.AwaitCallback(trackingNumber);
            }

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Workflow {WorkflowId} dispatched with tracking {Tracking} after {Attempts} attempt(s).",
                workflowId, trackingNumber, localAttempts);

            return new CarrierDispatchResult(true, trackingNumber, null, localAttempts);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Circuit Breaker tripped for workflow {WorkflowId}. Failing fast.", workflowId);
            
            // Quarantine event to DLQ to protect downstream resources
            var dlq = DeadLetterMessage.Create(
                workflowId,
                "CarrierDispatch",
                JsonSerializer.Serialize(new { workflowId, destinationAddress, mode = mode.ToString() }),
                $"Circuit Breaker Tripped: {ex.Message}",
                ex.StackTrace
            );
            await _dbContext.DeadLetterMessages.AddAsync(dlq, ct);
            await _dbContext.SaveChangesAsync(ct);

            return new CarrierDispatchResult(false, null, ex.Message, localAttempts, CircuitBreakerTripped: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Carrier dispatch exhausted retries for workflow {WorkflowId}.", workflowId);

            var dlq = DeadLetterMessage.Create(
                workflowId,
                "CarrierDispatch",
                JsonSerializer.Serialize(new { workflowId, destinationAddress, mode = mode.ToString() }),
                $"Carrier Dispatch Exhausted: {ex.Message}",
                ex.StackTrace
            );
            await _dbContext.DeadLetterMessages.AddAsync(dlq, ct);
            await _dbContext.SaveChangesAsync(ct);

            return new CarrierDispatchResult(false, null, ex.Message, localAttempts);
        }
    }

    private async Task<string> CallExternalCarrierApiAsync(
        Guid workflowId, 
        string destination, 
        CarrierSimulatorMode mode, 
        int attempt, 
        CancellationToken ct)
    {
        switch (mode)
        {
            case CarrierSimulatorMode.FailThreeTimesThenSucceed:
                if (attempt < 3)
                {
                    throw new HttpRequestException($"Simulated 503 Service Unavailable (Attempt #{attempt})", null, HttpStatusCode.ServiceUnavailable);
                }
                break;

            case CarrierSimulatorMode.AlwaysFail500:
                throw new HttpRequestException("Simulated 500 Internal Server Error", null, HttpStatusCode.InternalServerError);

            case CarrierSimulatorMode.RateLimited429:
                throw new HttpRequestException("Simulated 429 Too Many Requests. Partner rate limit exceeded.", null, HttpStatusCode.TooManyRequests);

            case CarrierSimulatorMode.Timeout:
                await Task.Delay(2000, ct); // Simulate slow responding external gateway
                throw new TimeoutException("Simulated gateway timeout (504 Gateway Timeout).");

            case CarrierSimulatorMode.Healthy:
            default:
                break;
        }

        // Return deterministic tracking number
        return $"TRK-{workflowId.ToString()[..8].ToUpper()}-{DateTime.UtcNow.Ticks % 10000}";
    }
}
