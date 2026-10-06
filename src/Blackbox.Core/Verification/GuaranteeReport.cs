namespace Blackbox.Core.Verification;

public record GuaranteePillarResult
{
    public required string PillarName { get; init; }
    public bool Evaluated { get; init; } = true;
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

    public int TotalInvariantsEvaluated => Pillars.Where(p => p.Evaluated).Sum(p => p.InvariantsEvaluated);
    public int TotalAssertionsEvaluated => Pillars.Where(p => p.Evaluated).Sum(p => p.AssertionsEvaluated);
    public int TotalViolations => Pillars.Sum(p => p.Violations.Count);
    public int EvaluatedPillarCount => Pillars.Count(p => p.Evaluated);
    public int NotEvaluatedPillarCount => Pillars.Count(p => !p.Evaluated);
    public bool OverallPassed => Pillars.All(p => !p.Evaluated || p.Passed) && TotalViolations == 0;

    public int TotalEventsPreserved { get; init; }
    public int TotalServicesInvolved { get; init; }
    public int TotalExternalDependencies { get; init; }

    /// <summary>
    /// Compact scenario verification summary:
    /// The counts describe only evidence supplied to the scenario verifier.
    /// </summary>
    public string SignatureSummary
    {
        get
        {
            string status = OverallPassed ? "PASS" : "FAIL";
            return $"{status}: {EvaluatedPillarCount}/{Pillars.Count} pillars evaluated | {TotalInvariantsEvaluated} invariants | {TotalAssertionsEvaluated} assertions | {TotalEventsPreserved} outbox events | {TotalServicesInvolved} observed services | {TotalExternalDependencies} declared external dependencies | {TotalViolations} violations";
        }
    }
}
