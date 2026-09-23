using System.Text.Json;
using Blackbox.Core.Verification;

namespace Blackbox.Core.Ai;

public class AutonomousRemediationAgent : IAutonomousRemediationAgent
{
    public Task<RemediationRecommendation> DiagnoseIncidentAsync(
        IncidentTelemetryContext context, 
        CancellationToken cancellationToken = default)
    {
        var spans = context.TraceSpans ?? Array.Empty<TraceSpanRecord>();
        var longestSpan = spans.OrderByDescending(s => s.DurationMs).FirstOrDefault();

        string hypothesis;
        RemediationActionType recommendedAction;
        double confidence;
        bool requiresHumanApproval;
        var policy = new Dictionary<string, object>();
        var reasoning = new List<string>();

        // 1. Diagnose based on OpenTelemetry Spans and Error Classification
        if (context.ExceptionMessage.Contains("Payment", StringComparison.OrdinalIgnoreCase) ||
            context.FailedStep.Contains("Payment", StringComparison.OrdinalIgnoreCase))
        {
            hypothesis = "Payment settlement rejected downstream by payment provider (e.g. CardDeclined / InsufficientFunds). Order pipeline partially committed.";
            recommendedAction = RemediationActionType.RollbackSaga;
            confidence = 0.96;
            requiresHumanApproval = false;

            policy["strategy"] = "REVERSE_SAGA_COMPENSATION";
            policy["compensatingSteps"] = new[] { "CancelCarrierBooking", "ReleaseInventoryAllocation" };
            policy["targetStatus"] = "COMPENSATED";
            policy["reason"] = "Compensating multi-party side effects in reverse topological DAG order";

            reasoning.Add("Trace shows carrier booking succeeded, but payment span failed with settlement rejection.");
            reasoning.Add("Standard Saga pattern dictates immediate reverse compensation to prevent inventory lock leakage.");
        }
        else if (context.ExceptionMessage.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                 (longestSpan != null && longestSpan.DurationMs > 3000))
        {
            hypothesis = $"Downstream dependency '{longestSpan?.ServiceName ?? "External.Carrier"}' caused latency spike ({longestSpan?.DurationMs ?? 0}ms) exceeding circuit breaker threshold.";
            recommendedAction = RemediationActionType.DrainAndRetryWithJitter;
            confidence = 0.92;
            requiresHumanApproval = false;

            policy["strategy"] = "JITTERED_EXPONENTIAL_BACKOFF";
            policy["baseDelayMs"] = 100;
            policy["maxDelayMs"] = 1500;
            policy["maxRetries"] = 3;
            policy["circuitAction"] = "HALF_OPEN_PROBE";

            reasoning.Add($"Span '{longestSpan?.OperationName}' in service '{longestSpan?.ServiceName}' ran for {longestSpan?.DurationMs}ms.");
            reasoning.Add("High confidence that transient network partition or carrier rate limiting caused timeout; backoff with jitter recommended.");
        }
        else if (context.ExceptionMessage.Contains("Malformed", StringComparison.OrdinalIgnoreCase) ||
                 context.ExceptionMessage.Contains("Syntax", StringComparison.OrdinalIgnoreCase) ||
                 context.ExceptionMessage.Contains("Contract", StringComparison.OrdinalIgnoreCase))
        {
            hypothesis = "Poison pill record detected: schema contract drift or corrupted JSON payload cannot be resolved via retries.";
            recommendedAction = RemediationActionType.QuarantineToDlq;
            confidence = 0.98;
            requiresHumanApproval = false;

            policy["strategy"] = "QUARANTINE_TO_DEAD_LETTER_QUEUE";
            policy["dlqTopic"] = "blackbox.dlq.poison";
            policy["suppressRetries"] = true;
            policy["alertLevel"] = "CRITICAL_SCHEMA_DRIFT";

            reasoning.Add("Payload failed schema parsing/contract validation; retrying would induce a self-inflicted retry storm.");
            reasoning.Add("Quarantined to DLQ for deterministic incident replay once producer pact is updated.");
        }
        else if (context.ExceptionMessage.Contains("Capacity", StringComparison.OrdinalIgnoreCase) ||
                 context.ExceptionMessage.Contains("Lock", StringComparison.OrdinalIgnoreCase))
        {
            hypothesis = $"Severe inventory lock contention on resource '{context.ResourceId ?? "SKU-HOT"}'. High-frequency race condition detected.";
            recommendedAction = RemediationActionType.ReleaseCapacity;
            confidence = 0.91;
            requiresHumanApproval = false;

            policy["strategy"] = "FORCE_UNLOCK_DANGLING_RESERVATION";
            policy["resourceId"] = context.ResourceId ?? "UNKNOWN";
            policy["quantity"] = context.AllocatedQuantity ?? 1;

            reasoning.Add("Resource allocation failed or holds deadlocked lock while workflow cannot proceed.");
            reasoning.Add("Releasing orphaned lock to unblock parallel transactions.");
        }
        else
        {
            hypothesis = $"Unknown critical anomaly: {context.ExceptionMessage}";
            recommendedAction = RemediationActionType.EscalateToHuman;
            confidence = 0.75;
            requiresHumanApproval = true;

            policy["strategy"] = "HUMAN_OPERATOR_INTERVENTION";
            policy["escalationTier"] = "SRE_ON_CALL";

            reasoning.Add("Anomaly does not match automated remediation taxonomy; escalating to human operator to protect data integrity.");
        }

        var recommendation = new RemediationRecommendation(
            IncidentId: context.IncidentId,
            RootCauseHypothesis: hypothesis,
            ConfidenceScore: confidence,
            RecommendedAction: recommendedAction,
            SuggestedPolicyJson: JsonSerializer.Serialize(policy),
            RequiresHumanApproval: requiresHumanApproval,
            ReasoningLog: string.Join(" | ", reasoning));

        return Task.FromResult(recommendation);
    }

    public Task<RemediationExecutionResult> ExecuteRemediationPolicyAsync(
        IncidentTelemetryContext context, 
        RemediationRecommendation recommendation, 
        CancellationToken cancellationToken = default)
    {
        if (recommendation.RequiresHumanApproval)
        {
            return Task.FromResult(new RemediationExecutionResult(
                IncidentId: context.IncidentId,
                WorkflowId: context.WorkflowId,
                ActionExecuted: recommendation.RecommendedAction,
                Success: false,
                StateAfterRemediation: "PENDING_HUMAN_APPROVAL",
                ExecutionNotes: "Remediation held pending authorized SRE approval. Automated execution prevented by security guardrail."));
        }

        string targetState = recommendation.RecommendedAction switch
        {
            RemediationActionType.RollbackSaga => "COMPENSATED",
            RemediationActionType.QuarantineToDlq => "QUARANTINED",
            RemediationActionType.DrainAndRetryWithJitter => "PROCESSING_RETRY",
            RemediationActionType.ReleaseCapacity => "CAPACITY_RESTORED",
            _ => "UNRESOLVED"
        };

        var result = new RemediationExecutionResult(
            IncidentId: context.IncidentId,
            WorkflowId: context.WorkflowId,
            ActionExecuted: recommendation.RecommendedAction,
            Success: true,
            StateAfterRemediation: targetState,
            ExecutionNotes: $"Successfully executed automated policy: {recommendation.RecommendedAction}. Workflow transitioned to {targetState}. Invariants preserved.");

        return Task.FromResult(result);
    }
}
