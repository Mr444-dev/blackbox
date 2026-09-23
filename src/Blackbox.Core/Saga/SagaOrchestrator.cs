using Blackbox.Core.Domain;
using Blackbox.Core.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Blackbox.Core.Saga;

public interface ISagaStep<TContext>
{
    string StepName { get; }
    Task<bool> ExecuteAsync(TContext context, CancellationToken cancellationToken = default);
    Task CompensateAsync(TContext context, CancellationToken cancellationToken = default);
}

public enum SagaExecutionStatus
{
    Completed,
    Compensated,
    Failed
}

public record SagaResult(
    bool Success,
    SagaExecutionStatus Status,
    string? FailedStep,
    string? Error,
    IReadOnlyList<string> ExecutedSteps,
    IReadOnlyList<string> CompensatedSteps
);

public class SagaOrchestrator<TContext>
{
    private readonly List<ISagaStep<TContext>> _steps = new();
    private readonly ILogger? _logger;

    public SagaOrchestrator(ILogger? logger = null)
    {
        _logger = logger;
    }

    public SagaOrchestrator<TContext> AddStep(ISagaStep<TContext> step)
    {
        _steps.Add(step);
        return this;
    }

    public async Task<SagaResult> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        var executedSteps = new Stack<ISagaStep<TContext>>();
        var executedStepNames = new List<string>();
        var compensatedStepNames = new List<string>();

        foreach (var step in _steps)
        {
            _logger?.LogInformation("[Saga Step] Executing: {StepName}", step.StepName);

            try
            {
                bool stepSucceeded = await step.ExecuteAsync(context, cancellationToken);
                if (!stepSucceeded)
                {
                    _logger?.LogWarning("[Saga Step Failed] Step '{StepName}' returned failure. Initiating rollback compensation...", step.StepName);
                    await RollbackAsync(context, executedSteps, compensatedStepNames, cancellationToken);
                    return new SagaResult(
                        Success: false,
                        Status: SagaExecutionStatus.Compensated,
                        FailedStep: step.StepName,
                        Error: $"Step '{step.StepName}' failed execution.",
                        ExecutedSteps: executedStepNames,
                        CompensatedSteps: compensatedStepNames
                    );
                }

                executedSteps.Push(step);
                executedStepNames.Add(step.StepName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Saga Step Exception] Step '{StepName}' threw exception. Initiating rollback compensation...", step.StepName);
                await RollbackAsync(context, executedSteps, compensatedStepNames, cancellationToken);
                return new SagaResult(
                    Success: false,
                    Status: SagaExecutionStatus.Compensated,
                    FailedStep: step.StepName,
                    Error: ex.Message,
                    ExecutedSteps: executedStepNames,
                    CompensatedSteps: compensatedStepNames
                );
            }
        }

        _logger?.LogInformation("[Saga Succeeded] All {Count} steps executed successfully.", _steps.Count);
        return new SagaResult(
            Success: true,
            Status: SagaExecutionStatus.Completed,
            FailedStep: null,
            Error: null,
            ExecutedSteps: executedStepNames,
            CompensatedSteps: compensatedStepNames
        );
    }

    private async Task RollbackAsync(
        TContext context, 
        Stack<ISagaStep<TContext>> executedSteps, 
        List<string> compensatedStepNames, 
        CancellationToken cancellationToken)
    {
        _logger?.LogWarning("[Saga Rollback] Rolling back {Count} previously succeeded step(s) in reverse order.", executedSteps.Count);

        while (executedSteps.Count > 0)
        {
            var step = executedSteps.Pop();
            _logger?.LogInformation("[Saga Compensating] Rolling back step: {StepName}", step.StepName);

            try
            {
                await step.CompensateAsync(context, cancellationToken);
                compensatedStepNames.Add(step.StepName);
            }
            catch (Exception ex)
            {
                _logger?.LogCritical(ex, "[Saga Fatal Compensation Failure] Failed to compensate step '{StepName}'. Manual intervention required.", step.StepName);
                // Continue rolling back remaining steps despite error
            }
        }
    }
}
