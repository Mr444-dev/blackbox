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

        // This demo classifier only uses supplied text and the longest supplied span.
        // It does not ingest telemetry or verify incident facts on its own.
        if (context.ExceptionMessage.Contains("Payment", StringComparison.OrdinalIgnoreCase) ||
            context.FailedStep.Contains("Payment", StringComparison.OrdinalIgnoreCase))
        {
            hypothesis = "Payment-related failure text was detected. The provider outcome and any earlier side effects have not been verified.";
            recommendedAction = RemediationActionType.RollbackSaga;
            confidence = 0.96;
            requiresHumanApproval = false;

            policy["strategy"] = "REVERSE_SAGA_COMPENSATION";
            policy["compensatingSteps"] = new[] { "CancelCarrierBooking", "ReleaseInventoryAllocation" };
            policy["targetStatus"] = "COMPENSATED";
            policy["reason"] = "Compensating multi-party side effects in reverse topological DAG order";

            reasoning.Add("The supplied exception text or failed-step label matched the Payment keyword.");
            reasoning.Add("Saga compensation is a proposed policy; no prior side effect or payment-provider response was inspected.");
        }
        else if (context.ExceptionMessage.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                 (longestSpan != null && double.IsFinite(longestSpan.DurationMs) && longestSpan.DurationMs > 3000))
        {
            var spanExceededThreshold = longestSpan is not null &&
                double.IsFinite(longestSpan.DurationMs) &&
                longestSpan.DurationMs > 3000;
            hypothesis = spanExceededThreshold
                ? $"The longest supplied span for '{longestSpan!.ServiceName}' exceeded the demonstration threshold of 3000 ms ({longestSpan!.DurationMs:F0} ms). This does not establish the root cause."
                : "The supplied exception message matched the Timeout keyword. The cause and affected dependency have not been verified.";
            recommendedAction = RemediationActionType.DrainAndRetryWithJitter;
            confidence = 0.92;
            requiresHumanApproval = false;

            policy["strategy"] = "JITTERED_EXPONENTIAL_BACKOFF";
            policy["baseDelayMs"] = 100;
            policy["maxDelayMs"] = 1500;
            policy["maxRetries"] = 3;
            policy["circuitAction"] = "HALF_OPEN_PROBE";

            if (spanExceededThreshold)
                reasoning.Add($"The longest supplied span was '{longestSpan!.OperationName}' in '{longestSpan!.ServiceName}' at {longestSpan!.DurationMs:F0} ms.");
            else
                reasoning.Add("The supplied exception message matched the Timeout keyword; no trace span established a cause.");
            reasoning.Add("Jittered backoff is a proposal only; no retry was scheduled.");
        }
        else if (context.ExceptionMessage.Contains("Malformed", StringComparison.OrdinalIgnoreCase) ||
                 context.ExceptionMessage.Contains("Syntax", StringComparison.OrdinalIgnoreCase) ||
                 context.ExceptionMessage.Contains("Contract", StringComparison.OrdinalIgnoreCase))
        {
            hypothesis = "The supplied exception text matched a malformed, syntax, or contract keyword. Payload validation was not performed by this classifier.";
            recommendedAction = RemediationActionType.QuarantineToDlq;
            confidence = 0.98;
            requiresHumanApproval = false;

            policy["strategy"] = "QUARANTINE_TO_DEAD_LETTER_QUEUE";
            policy["dlqTopic"] = "blackbox.dlq.poison";
            policy["suppressRetries"] = true;
            policy["alertLevel"] = "CRITICAL_SCHEMA_DRIFT";

            reasoning.Add("The supplied exception text matched a malformed, syntax, or contract keyword.");
            reasoning.Add("DLQ quarantine is a proposal only; no Kafka record was moved or replayed.");
        }
        else if (context.ExceptionMessage.Contains("Capacity", StringComparison.OrdinalIgnoreCase) ||
                 context.ExceptionMessage.Contains("Lock", StringComparison.OrdinalIgnoreCase))
        {
            hypothesis = $"The supplied exception text matched a capacity or lock keyword. Resource '{context.ResourceId ?? "not supplied"}' state and lock ownership have not been inspected.";
            recommendedAction = RemediationActionType.ReleaseCapacity;
            confidence = 0.91;
            requiresHumanApproval = false;

            policy["strategy"] = "FORCE_UNLOCK_DANGLING_RESERVATION";
            policy["resourceId"] = context.ResourceId ?? "UNKNOWN";
            if (context.AllocatedQuantity is { } allocatedQuantity)
                policy["quantity"] = allocatedQuantity;

            reasoning.Add("The supplied exception text matched the Capacity or Lock keyword.");
            reasoning.Add("No reservation or lock state was inspected; releasing capacity is a proposal only.");
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

        reasoning.Add("ConfidenceScore is a fixed demonstration value, not a calibrated probability.");

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

        var result = new RemediationExecutionResult(
            IncidentId: context.IncidentId,
            WorkflowId: context.WorkflowId,
            ActionExecuted: recommendation.RecommendedAction,
            Success: false,
            StateAfterRemediation: "NOT_EXECUTED",
            ExecutionNotes: "This prototype only classifies incidents and proposes a policy. It has no workflow, carrier, inventory, or DLQ command handler, so no remediation was executed.");

        return Task.FromResult(result);
    }
}
