using Blackbox.Core.Ai;
using Blackbox.Core.Verification;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class AutonomousRemediationAgentTests
{
    private readonly AutonomousRemediationAgent _agent = new();

    [Fact]
    public async Task DiagnoseIncident_WhenPaymentFails_RecommendsSagaRollbackAndExecutesAutonomously()
    {
        // Arrange
        var context = new IncidentTelemetryContext(
            IncidentId: "INC-AI-101",
            WorkflowId: Guid.NewGuid(),
            CorrelationId: "corr-ai-payment-fail",
            FailedStep: "ProcessPayment",
            ExceptionMessage: "Payment authorization rejected: InsufficientFunds on credit card",
            TraceSpans: new List<TraceSpanRecord>
            {
                new("4bf92f3577b34da6a3ce929d0e0e4736", "span-1", null, "Blackbox.Api", "POST /saga", 25),
                new("4bf92f3577b34da6a3ce929d0e0e4736", "span-2", "span-1", "Blackbox.Worker", "ReserveInventory", 40),
                new("4bf92f3577b34da6a3ce929d0e0e4736", "span-3", "span-1", "Blackbox.Worker", "DispatchCarrier", 85),
                new("4bf92f3577b34da6a3ce929d0e0e4736", "span-4", "span-1", "External.PaymentGateway", "ProcessPayment", 310)
            },
            HistoricalTransitions: new List<string> { "Created", "Validated", "Reserved", "Processing", "Failed" }
        );

        // Act 1: Diagnose
        var recommendation = await _agent.DiagnoseIncidentAsync(context);

        // Assert Diagnosis
        recommendation.Should().NotBeNull();
        recommendation.IncidentId.Should().Be("INC-AI-101");
        recommendation.RecommendedAction.Should().Be(RemediationActionType.RollbackSaga);
        recommendation.ConfidenceScore.Should().BeGreaterThan(0.90);
        recommendation.RequiresHumanApproval.Should().BeFalse();
        recommendation.SuggestedPolicyJson.Should().Contain("REVERSE_SAGA_COMPENSATION");

        // Act 2: Execute Remediation
        var execution = await _agent.ExecuteRemediationPolicyAsync(context, recommendation);

        // Assert Execution
        execution.Success.Should().BeTrue();
        execution.StateAfterRemediation.Should().Be("COMPENSATED");
        execution.ExecutionNotes.Should().Contain("Invariants preserved");
    }

    [Fact]
    public async Task DiagnoseIncident_WhenCarrierTimesOut_RecommendsJitteredBackoff()
    {
        // Arrange
        var context = new IncidentTelemetryContext(
            IncidentId: "INC-AI-102",
            WorkflowId: Guid.NewGuid(),
            CorrelationId: "corr-ai-carrier-timeout",
            FailedStep: "DispatchCarrier",
            ExceptionMessage: "HTTP 504 Gateway Timeout from 3PL Carrier API after 5000ms",
            TraceSpans: new List<TraceSpanRecord>
            {
                new("4bf92f3577b34da6a3ce929d0e0e4736", "span-1", null, "Blackbox.Api", "POST /workflows/dispatch", 15),
                new("4bf92f3577b34da6a3ce929d0e0e4736", "span-carrier", "span-1", "External.Carrier", "BookShipment", 5400) // 5.4s spike!
            },
            HistoricalTransitions: new List<string> { "Created", "Validated", "Reserved" }
        );

        // Act
        var recommendation = await _agent.DiagnoseIncidentAsync(context);

        // Assert
        recommendation.RecommendedAction.Should().Be(RemediationActionType.DrainAndRetryWithJitter);
        recommendation.ConfidenceScore.Should().BeGreaterThan(0.90);
        recommendation.SuggestedPolicyJson.Should().Contain("JITTERED_EXPONENTIAL_BACKOFF");
    }

    [Fact]
    public async Task DiagnoseIncident_WhenMalformedJsonPoisonPill_RecommendsQuarantineToDlq()
    {
        // Arrange
        var context = new IncidentTelemetryContext(
            IncidentId: "INC-AI-103",
            WorkflowId: Guid.NewGuid(),
            CorrelationId: "corr-ai-poison-pill",
            FailedStep: "IngestKafkaEvent",
            ExceptionMessage: "Malformed JSON payload: unexpected character at token 12",
            TraceSpans: new List<TraceSpanRecord>(),
            HistoricalTransitions: new List<string> { "Created" }
        );

        // Act
        var recommendation = await _agent.DiagnoseIncidentAsync(context);

        // Assert
        recommendation.RecommendedAction.Should().Be(RemediationActionType.QuarantineToDlq);
        recommendation.SuggestedPolicyJson.Should().Contain("QUARANTINE_TO_DEAD_LETTER_QUEUE");
        recommendation.SuggestedPolicyJson.Should().Contain("\"suppressRetries\":true");
    }

    [Fact]
    public async Task DiagnoseIncident_WhenUnknownAnomaly_EscalatesToHumanOperator()
    {
        // Arrange
        var context = new IncidentTelemetryContext(
            IncidentId: "INC-AI-104",
            WorkflowId: Guid.NewGuid(),
            CorrelationId: "corr-ai-unknown",
            FailedStep: "CustomPluginStep",
            ExceptionMessage: "Unrecognized proprietary hardware driver error 0x8849F",
            TraceSpans: new List<TraceSpanRecord>(),
            HistoricalTransitions: new List<string> { "Created" }
        );

        // Act 1: Diagnose
        var recommendation = await _agent.DiagnoseIncidentAsync(context);

        // Assert
        recommendation.RecommendedAction.Should().Be(RemediationActionType.EscalateToHuman);
        recommendation.RequiresHumanApproval.Should().BeTrue();

        // Act 2: Attempt execution without human approval
        var execution = await _agent.ExecuteRemediationPolicyAsync(context, recommendation);

        // Assert automated execution was blocked by safety guardrail
        execution.Success.Should().BeFalse();
        execution.StateAfterRemediation.Should().Be("PENDING_HUMAN_APPROVAL");
        execution.ExecutionNotes.Should().Contain("prevented by security guardrail");
    }
}
