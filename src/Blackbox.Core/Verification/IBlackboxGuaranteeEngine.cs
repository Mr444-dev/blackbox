namespace Blackbox.Core.Verification;

public interface IBlackboxGuaranteeEngine
{
    Task<GuaranteeReport> VerifyWorkflowScenarioAsync(
        WorkflowScenarioContext context, 
        CancellationToken cancellationToken = default);
}
