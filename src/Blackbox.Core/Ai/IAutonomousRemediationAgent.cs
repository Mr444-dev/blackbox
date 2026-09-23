namespace Blackbox.Core.Ai;

public interface IAutonomousRemediationAgent
{
    Task<RemediationRecommendation> DiagnoseIncidentAsync(
        IncidentTelemetryContext context, 
        CancellationToken cancellationToken = default);

    Task<RemediationExecutionResult> ExecuteRemediationPolicyAsync(
        IncidentTelemetryContext context, 
        RemediationRecommendation recommendation, 
        CancellationToken cancellationToken = default);
}
