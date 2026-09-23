namespace Blackbox.Core.Verification;

public record GuaranteePillarResult
{
    public required string PillarName { get; init; }
    public required bool Passed { get; init; }
    public required string Summary { get; init; }
    public int InvariantsEvaluated { get; init; }
    public int AssertionsEvaluated { get; init; }
    public List<string> Details { get; init; } = new();
    public List<string> Violations { get; init; } = new();
}

public class GuaranteeReport
{
    public required string ScenarioName { get; init; }
    public required Guid WorkflowId { get; init; }
    public required string CorrelationId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double ExecutionDurationMs { get; init; }
    public List<GuaranteePillarResult> Pillars { get; init; } = new();

    public int TotalInvariantsEvaluated => Pillars.Sum(p => p.InvariantsEvaluated);
    public int TotalAssertionsEvaluated => Pillars.Sum(p => p.AssertionsEvaluated);
    public int TotalViolations => Pillars.Sum(p => p.Violations.Count);
    public bool OverallPassed => Pillars.All(p => p.Passed) && TotalViolations == 0;

    public int TotalEventsPreserved { get; init; }
    public int TotalServicesInvolved { get; init; } = 3; // e.g. Api, Core/Worker, Persistence
    public int TotalExternalDependencies { get; init; } = 2; // e.g. Carrier, Payment

    /// <summary>
    /// Signature formal guarantee string:
    /// e.g. "PASS: 13 invariants | 48 assertions | 12 events | 3 services | 2 external dependencies | 0 violations"
    /// </summary>
    public string SignatureSummary
    {
        get
        {
            string status = OverallPassed ? "PASS" : "FAIL";
            return $"{status}: {TotalInvariantsEvaluated} invariants | {TotalAssertionsEvaluated} assertions | {TotalEventsPreserved} events | {TotalServicesInvolved} services | {TotalExternalDependencies} external dependencies | {TotalViolations} violations";
        }
    }
}
