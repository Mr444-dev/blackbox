using Blackbox.Core.Verification;

namespace Blackbox.Core.Ai;

public enum RemediationActionType
{
    RollbackSaga,
    QuarantineToDlq,
    DrainAndRetryWithJitter,
    ReleaseCapacity,
    EscalateToHuman
}

public record IncidentTelemetryContext(
    string IncidentId,
    Guid WorkflowId,
    string CorrelationId,
    string FailedStep,
    string ExceptionMessage,
    IReadOnlyList<TraceSpanRecord> TraceSpans,
    IReadOnlyList<string> HistoricalTransitions,
    string? ResourceId = null,
    int? AllocatedQuantity = null,
    Dictionary<string, string>? Metadata = null);

public record RemediationRecommendation(
    string IncidentId,
    string RootCauseHypothesis,
    double ConfidenceScore,
    RemediationActionType RecommendedAction,
    string SuggestedPolicyJson,
    bool RequiresHumanApproval,
    string ReasoningLog);

public record RemediationExecutionResult(
    string IncidentId,
    Guid WorkflowId,
    RemediationActionType ActionExecuted,
    bool Success,
    string StateAfterRemediation,
    string ExecutionNotes);
