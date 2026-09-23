using Blackbox.Core.Domain;
using Blackbox.Core.Verification;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class GuaranteeEngineTests
{
    private readonly BlackboxGuaranteeEngine _engine = new();

    [Fact]
    public async Task VerifyWorkflowScenarioAsync_WhenValidScenario_ProducesPassedSignatureReport()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("corr-12345", "{\"orderId\":\"ord-99\",\"amount\":150.00}");
        workflow.Validate();
        workflow.Reserve();
        workflow.StartProcessing();
        workflow.Complete();

        var resource = new Resource("inventory-gpu-rtx4090", "RTX 4090", 10);
        var res = resource.Reserve(workflow.Id, 1);
        resource.ConsumeReservation(res);

        var context = new WorkflowScenarioContext
        {
            ScenarioName = "HappyPath_OrderFulfillment",
            Workflow = workflow,
            Resource = resource,
            OutboxMessageIds = new List<string> { "msg-1", "msg-2", "msg-3" },
            InboxProcessedMessageIds = new List<string> { "inbox-101", "inbox-102" },
            SideEffects = new List<SideEffectRecord>
            {
                new("CARRIER_DISPATCH", "idem-carrier-1", DateTime.UtcNow, true, "{\"carrier\":\"DHL\"}"),
                new("PAYMENT_CAPTURE", "idem-pay-1", DateTime.UtcNow, true, "{\"gateway\":\"STRIPE\"}")
            },
            TraceSpans = new List<TraceSpanRecord>
            {
                new("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7", null, "Blackbox.Api", "POST /orders", 45),
                new("4bf92f3577b34da6a3ce929d0e0e4736", "5fb3acbe4223fd36", "00f067aa0ba902b7", "Blackbox.Worker", "Kafka:ProcessEvent", 120)
            },
            ExecutionDurationMs = 165.0,
            SecurityAudit = new SecurityAuditRecord("svc_account", true, true, true, true)
        };

        // Act
        var report = await _engine.VerifyWorkflowScenarioAsync(context);

        // Assert
        report.Should().NotBeNull();
        report.OverallPassed.Should().BeTrue();
        report.TotalViolations.Should().Be(0);
        report.Pillars.Should().HaveCount(10);
        report.Pillars.All(p => p.Passed).Should().BeTrue();
        report.SignatureSummary.Should().StartWith("PASS:");
        report.SignatureSummary.Should().Contain("invariants");
        report.SignatureSummary.Should().Contain("0 violations");
    }

    [Fact]
    public async Task VerifyWorkflowScenarioAsync_WhenDuplicateSideEffects_FlagsPillarFailure()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("corr-duplicate", "{\"orderId\":\"ord-dup\"}");
        workflow.Validate();

        var context = new WorkflowScenarioContext
        {
            ScenarioName = "DuplicateSideEffect_Anomaly",
            Workflow = workflow,
            SideEffects = new List<SideEffectRecord>
            {
                new("CARRIER_DISPATCH", "idem-same-key", DateTime.UtcNow, true, null),
                new("CARRIER_DISPATCH", "idem-same-key", DateTime.UtcNow, true, null) // DUPLICATE!
            },
            ExecutionDurationMs = 80.0
        };

        // Act
        var report = await _engine.VerifyWorkflowScenarioAsync(context);

        // Assert
        report.OverallPassed.Should().BeFalse();
        report.TotalViolations.Should().BeGreaterThan(0);
        var sideEffectPillar = report.Pillars.First(p => p.PillarName == "No Duplicate Side Effects");
        sideEffectPillar.Passed.Should().BeFalse();
        sideEffectPillar.Violations.Should().Contain(v => v.Contains("Duplicate side-effect execution"));
        report.SignatureSummary.Should().StartWith("FAIL:");
    }

    [Fact]
    public async Task VerifyWorkflowScenarioAsync_WhenSloExceeded_FlagsSloPillar()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("corr-slo", "{\"orderId\":\"ord-slow\"}");
        workflow.Validate();

        var context = new WorkflowScenarioContext
        {
            ScenarioName = "SlowProcessing_SloBreach",
            Workflow = workflow,
            ExecutionDurationMs = 3500.0 // Exceeds 2000ms P95 budget!
        };

        // Act
        var report = await _engine.VerifyWorkflowScenarioAsync(context);

        // Assert
        report.OverallPassed.Should().BeFalse();
        var sloPillar = report.Pillars.First(p => p.PillarName == "SLO Compliance");
        sloPillar.Passed.Should().BeFalse();
        sloPillar.Violations.Should().Contain(v => v.Contains("breached P95 SLO threshold"));
    }

    [Fact]
    public async Task VerifyWorkflowScenarioAsync_WhenUnauthenticated_FlagsSecurityPillar()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("corr-sec", "{\"orderId\":\"ord-hack\"}");
        workflow.Validate();

        var context = new WorkflowScenarioContext
        {
            ScenarioName = "SecurityBreach_Unauthenticated",
            Workflow = workflow,
            SecurityAudit = new SecurityAuditRecord("anonymous", false, false, true, true),
            ExecutionDurationMs = 50.0
        };

        // Act
        var report = await _engine.VerifyWorkflowScenarioAsync(context);

        // Assert
        report.OverallPassed.Should().BeFalse();
        var secPillar = report.Pillars.First(p => p.PillarName == "Security Rules");
        secPillar.Passed.Should().BeFalse();
        secPillar.Violations.Should().Contain("Caller must be authenticated.");
    }
}
