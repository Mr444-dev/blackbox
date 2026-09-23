using Blackbox.Core.Domain;

namespace Blackbox.Core.Verification;

public record SideEffectRecord(string EffectType, string IdempotencyKey, DateTime ExecutedAt, bool Successful, string? Payload);

public record TraceSpanRecord(string TraceId, string SpanId, string? ParentSpanId, string ServiceName, string OperationName, long DurationMs);

public record SecurityAuditRecord(string CallerIdentity, bool IsAuthenticated, bool HasValidRole, bool RateLimitPassed, bool PayloadSanitized);

public class WorkflowScenarioContext
{
    public required string ScenarioName { get; init; }
    public required WorkflowInstance Workflow { get; init; }
    public Resource? Resource { get; init; }
    public List<string> OutboxMessageIds { get; init; } = new();
    public List<string> InboxProcessedMessageIds { get; init; } = new();
    public List<SideEffectRecord> SideEffects { get; init; } = new();
    public List<TraceSpanRecord> TraceSpans { get; init; } = new();
    public double ExecutionDurationMs { get; init; }
    public SecurityAuditRecord SecurityAudit { get; init; } = new("service_account_workflow", true, true, true, true);
    public Dictionary<string, string> Metadata { get; init; } = new();
}
