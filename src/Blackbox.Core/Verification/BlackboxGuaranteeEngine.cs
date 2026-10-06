using System.Text.Json;
using Blackbox.Core.Domain;
using Blackbox.Core.Domain.Events;

namespace Blackbox.Core.Verification;

public class BlackboxGuaranteeEngine : IBlackboxGuaranteeEngine
{
    public Task<GuaranteeReport> VerifyWorkflowScenarioAsync(
        WorkflowScenarioContext context, 
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var pillars = new List<GuaranteePillarResult>();

        // 1. API Contract Verification
        pillars.Add(VerifyApiContract(context));

        // 2. Database State Integrity
        pillars.Add(VerifyDatabaseState(context));

        // 3. Event Sequence & Outbox Consistency
        pillars.Add(VerifyEventSequence(context));

        // 4. State Machine Transition DAG
        pillars.Add(VerifyStateMachine(context));

        // 5. Business Invariants (Concurrency & Capacity)
        pillars.Add(VerifyBusinessInvariants(context));

        // 6. Security Rules & Sanitization
        pillars.Add(VerifySecurityRules(context));

        // 7. Idempotency & Message Deduplication
        pillars.Add(VerifyIdempotency(context));

        // 8. No Duplicate Side Effects
        pillars.Add(VerifySideEffects(context));

        // 9. SLO Compliance (Latency & Error Budget)
        pillars.Add(VerifyScenarioLatency(context));

        // 10. Distributed Trace Completeness
        pillars.Add(VerifyTraceCompleteness(context));

        var report = new GuaranteeReport
        {
            ScenarioName = context.ScenarioName,
            WorkflowId = context.Workflow.Id,
            CorrelationId = context.Workflow.CorrelationId,
            ExecutionDurationMs = context.ExecutionDurationMs,
            Pillars = pillars,
            TotalEventsPreserved = context.OutboxMessageIds.Count,
            TotalServicesInvolved = context.TraceSpans.Select(s => s.ServiceName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count(),
            TotalExternalDependencies = Math.Max(0, context.ExternalDependencyCount)
        };

        return Task.FromResult(report);
    }

    private static GuaranteePillarResult VerifyApiContract(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        assertions++;
        if (ctx.Workflow.Id == Guid.Empty)
            violations.Add("Workflow Id cannot be Guid.Empty.");
        else
            details.Add($"Valid Workflow UUID: {ctx.Workflow.Id}");

        assertions++;
        if (string.IsNullOrWhiteSpace(ctx.Workflow.CorrelationId))
            violations.Add("CorrelationId cannot be null or empty.");
        else
            details.Add($"Valid CorrelationId: {ctx.Workflow.CorrelationId}");

        assertions++;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Workflow.PayloadJson);
            details.Add("PayloadJson is valid JSON.");
        }
        catch (Exception ex)
        {
            violations.Add($"PayloadJson is not valid JSON: {ex.Message}");
        }

        return new GuaranteePillarResult
        {
            PillarName = "API Contract",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0
                ? "Workflow identifiers and payload JSON syntax checks passed."
                : "Workflow identifier or payload JSON syntax checks failed.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyDatabaseState(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        assertions++;
        if (ctx.Workflow.Version < 1)
            violations.Add($"Workflow version must be >= 1. Actual: {ctx.Workflow.Version}");
        else
            details.Add($"Workflow monotonic version = {ctx.Workflow.Version}");

        assertions++;
        if (ctx.Workflow.CreatedAt > ctx.Workflow.UpdatedAt)
            violations.Add("CreatedAt cannot be after UpdatedAt.");
        else
            details.Add("Timestamp temporal order preserved (CreatedAt <= UpdatedAt).");

        return new GuaranteePillarResult
        {
            PillarName = "Workflow State Consistency",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "Workflow object version and timestamp checks passed." : "Workflow state consistency violations detected.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyEventSequence(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        var events = ctx.Workflow.DomainEvents.ToList();
        assertions++;
        if (events.Count > 0 && events[0] is not WorkflowCreatedEvent)
            violations.Add($"Initial event must be WorkflowCreatedEvent. Actual: {events[0].GetType().Name}");
        else if (events.Count > 0)
            details.Add("Initial event is WorkflowCreatedEvent.");

        assertions++;
        if (ctx.Workflow.Status == WorkflowStatus.Completed && !events.Any(e => e is WorkflowCompletedEvent))
            violations.Add("Workflow is Completed but WorkflowCompletedEvent is missing.");
        else if (ctx.Workflow.Status == WorkflowStatus.Completed)
            details.Add("Terminal WorkflowCompletedEvent present.");

        assertions++;
        if (ctx.Workflow.Status == WorkflowStatus.Failed && !events.Any(e => e is WorkflowFailedEvent))
            violations.Add("Workflow is Failed but WorkflowFailedEvent is missing.");
        else if (ctx.Workflow.Status == WorkflowStatus.Failed)
            details.Add("Terminal WorkflowFailedEvent present.");

        return new GuaranteePillarResult
        {
            PillarName = "Event Sequence",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "Domain event ordering checks passed for the events present in the scenario." : "Domain event sequence violation.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyStateMachine(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 2; // (1) Legal transitions only; (2) Terminal states immutable

        assertions++;
        bool isTerminal = WorkflowInstance.IsTerminalState(ctx.Workflow.Status);
        details.Add($"Current state '{ctx.Workflow.Status}' (IsTerminal: {isTerminal})");

        assertions++;
        if (isTerminal)
        {
            // Verify terminal state immutability invariant
            try
            {
                ctx.Workflow.Validate();
                violations.Add("Invariant breached: Terminal state permitted transition to Validated!");
            }
            catch (WorkflowInvariantException)
            {
                details.Add("Terminal state immutability verified: illegal transition properly rejected.");
            }
        }
        else
        {
            details.Add("Active state machine within legal operational lifecycle.");
        }

        return new GuaranteePillarResult
        {
            PillarName = "State Machine Transitions",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "The supplied workflow snapshot passed the terminal-state guard check." : "State machine guard violation.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyBusinessInvariants(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 3; // Available, allocated, and consumed quantities must be non-negative.

        if (ctx.Resource != null)
        {
            assertions++;
            if (ctx.Resource.AvailableQuantity < 0)
                violations.Add($"Available quantity negative: {ctx.Resource.AvailableQuantity}");
            else
                details.Add($"Available quantity non-negative: {ctx.Resource.AvailableQuantity}");

            assertions++;
            if (ctx.Resource.AllocatedQuantity < 0)
                violations.Add($"Allocated quantity negative: {ctx.Resource.AllocatedQuantity}");
            else
                details.Add($"Allocated quantity non-negative: {ctx.Resource.AllocatedQuantity}");

            assertions++;
            if (ctx.Resource.ConsumedQuantity < 0)
                violations.Add($"Consumed quantity negative: {ctx.Resource.ConsumedQuantity}");
            else
                details.Add($"Consumed quantity non-negative: {ctx.Resource.ConsumedQuantity}");
        }
        else
        {
            return new GuaranteePillarResult
            {
                PillarName = "Business Invariants",
                Evaluated = false,
                Passed = false,
                Summary = "Not evaluated: no resource state was supplied.",
                Details = details
            };
        }

        return new GuaranteePillarResult
        {
            PillarName = "Business Invariants",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "Non-negative inventory quantity checks passed for the supplied resource snapshot." : "Inventory quantity invariant violations detected.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifySecurityRules(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        var audit = ctx.SecurityAudit;
        if (audit is null || (audit.IsAuthenticated is null && audit.HasValidRole is null && audit.RateLimitPassed is null && audit.PayloadSanitized is null))
        {
            return new GuaranteePillarResult
            {
                PillarName = "Security Scenario Checks",
                Evaluated = false,
                Passed = false,
                Summary = "Not evaluated: no security signals were supplied.",
                Details = details
            };
        }

        if (audit.IsAuthenticated is bool authenticated)
        {
            assertions++;
            if (!authenticated)
                violations.Add("Scenario input reports an unauthenticated caller.");
            else
                details.Add($"Scenario input reports authenticated caller '{audit.CallerIdentity}'.");
        }
        if (audit.HasValidRole is bool hasValidRole)
        {
            assertions++;
            if (!hasValidRole)
                violations.Add("Scenario input reports that the caller lacks the required role.");
            else
                details.Add("Scenario input reports the required role.");
        }
        if (audit.RateLimitPassed is bool rateLimitPassed)
        {
            assertions++;
            if (!rateLimitPassed)
                violations.Add("Scenario input reports that the rate limit was exceeded.");
            else
                details.Add("Scenario input reports that the rate limit was not exceeded.");
        }
        if (audit.PayloadSanitized is bool payloadSanitized)
        {
            assertions++;
            if (!payloadSanitized)
                violations.Add("Scenario input reports that payload sanitization failed.");
            else
                details.Add("Scenario input reports payload sanitization passed; the engine does not scan the payload.");
        }

        return new GuaranteePillarResult
        {
            PillarName = "Security Scenario Checks",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "Supplied security scenario signals passed; no independent security controls were tested." : "Supplied security scenario signals include a violation.",
            InvariantsEvaluated = assertions,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyIdempotency(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        if (ctx.InboxMessageIds.Count == 0)
        {
            return new GuaranteePillarResult
            {
                PillarName = "Idempotency & Deduplication",
                Evaluated = false,
                Passed = false,
                Summary = "Not evaluated: no Inbox message IDs were supplied.",
                Details = details
            };
        }

        assertions++;
        var duplicateInboxIds = ctx.InboxMessageIds
            .GroupBy(x => x)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateInboxIds.Count > 0)
            violations.Add($"Duplicate message IDs processed in inbox: {string.Join(", ", duplicateInboxIds)}");
        else
            details.Add($"No duplicate IDs were found among {ctx.InboxMessageIds.Count} supplied Inbox receipts.");

        return new GuaranteePillarResult
        {
            PillarName = "Idempotency & Deduplication",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "No duplicate IDs were found in the supplied Inbox sample." : "Duplicate IDs were found in the supplied Inbox sample.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifySideEffects(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        if (ctx.SideEffects.Count == 0)
        {
            return new GuaranteePillarResult
            {
                PillarName = "No Duplicate Side Effects",
                Evaluated = false,
                Passed = false,
                Summary = "Not evaluated: no side-effect records were supplied.",
                Details = details
            };
        }

        assertions++;
        var duplicateKeys = ctx.SideEffects
            .GroupBy(s => (s.EffectType, s.IdempotencyKey))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.EffectType}:{g.Key.IdempotencyKey}")
            .ToList();

        if (duplicateKeys.Count > 0)
            violations.Add($"Duplicate side-effect execution detected for IdempotencyKey: {string.Join(", ", duplicateKeys)}");
        else
            details.Add($"No duplicate (effect type, idempotency key) pairs were found in {ctx.SideEffects.Count} supplied records.");

        return new GuaranteePillarResult
        {
            PillarName = "No Duplicate Side Effects",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "The supplied side-effect sample contains no duplicate keys." : "Duplicate side effects were found in the supplied sample.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyScenarioLatency(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        assertions++;
        const double ScenarioLatencyLimitMs = 2000.0;
        if (!double.IsFinite(ctx.ExecutionDurationMs) || ctx.ExecutionDurationMs < 0)
            violations.Add($"Execution duration must be a finite non-negative value. Actual: {ctx.ExecutionDurationMs}ms.");
        else if (ctx.ExecutionDurationMs > ScenarioLatencyLimitMs)
            violations.Add($"Scenario duration ({ctx.ExecutionDurationMs:F1}ms) exceeded the configured limit ({ScenarioLatencyLimitMs:F1}ms).");
        else
            details.Add($"Single scenario duration {ctx.ExecutionDurationMs:F1}ms is within the configured limit. This is not a P95 or availability measurement.");

        return new GuaranteePillarResult
        {
            PillarName = "Scenario Latency Budget",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "The single-run scenario duration is within its configured limit; production SLOs are not measured here." : "Scenario latency check failed.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }

    private static GuaranteePillarResult VerifyTraceCompleteness(WorkflowScenarioContext ctx)
    {
        var details = new List<string>();
        var violations = new List<string>();
        int assertions = 0;
        int invariants = 1;

        assertions++;
        if (ctx.TraceSpans.Count == 0)
        {
            return new GuaranteePillarResult
            {
                PillarName = "Distributed Trace Completeness",
                Evaluated = false,
                Passed = false,
                Summary = "Not evaluated: no trace spans were supplied.",
                Details = details
            };
        }
        else
        {
            var distinctTraceIds = ctx.TraceSpans.Select(s => s.TraceId).Distinct().ToList();
            if (distinctTraceIds.Count > 1 || distinctTraceIds.Any(string.IsNullOrWhiteSpace))
                violations.Add($"Distributed trace fragmented across multiple TraceIds: {string.Join(", ", distinctTraceIds)}");
            else if (ctx.TraceSpans.Any(s => string.IsNullOrWhiteSpace(s.SpanId)) || ctx.TraceSpans.Select(s => s.SpanId).Distinct().Count() != ctx.TraceSpans.Count)
                violations.Add("Trace contains an empty or duplicate span ID.");
            else
                details.Add($"All {ctx.TraceSpans.Count} supplied spans share TraceId {distinctTraceIds[0]}, with unique span IDs.");
        }

        return new GuaranteePillarResult
        {
            PillarName = "Distributed Trace Completeness",
            Passed = violations.Count == 0,
            Summary = violations.Count == 0 ? "The supplied spans share a trace ID; this does not verify unreported services." : "The supplied trace sample is inconsistent.",
            InvariantsEvaluated = invariants,
            AssertionsEvaluated = assertions,
            Details = details,
            Violations = violations
        };
    }
}
