using System.Text.Json;
using Blackbox.Core.Domain;
using Blackbox.Core.Domain.Events;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class WorkflowStateMachineTests
{
    [Fact]
    public void Create_WithValidCorrelationId_ShouldInitializeInCreatedState()
    {
        // Arrange
        var correlationId = "CORR-001";
        var payload = "{\"orderId\":\"ORD-123\",\"amount\":150.00}";

        // Act
        var workflow = WorkflowInstance.Create(correlationId, payload);

        // Assert
        workflow.Should().NotBeNull();
        workflow.Status.Should().Be(WorkflowStatus.Created);
        workflow.CorrelationId.Should().Be(correlationId);
        workflow.PayloadJson.Should().Be(payload);
        workflow.Version.Should().Be(1);
        workflow.DomainEvents.Should().ContainSingle(e => e is WorkflowCreatedEvent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithInvalidCorrelationId_ShouldThrowInvariantException(string? invalidCorrelationId)
    {
        // Act
        Action act = () => WorkflowInstance.Create(invalidCorrelationId!, "{}");

        // Assert
        act.Should().Throw<WorkflowInvariantException>()
           .WithMessage("*CorrelationIdRequired*");
    }

    [Fact]
    public void ValidTransitions_ShouldProgressThroughHappyPathSuccessfully()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("CORR-HAPPY", "{}");

        // Act & Assert Transitions
        workflow.Validate();
        workflow.Status.Should().Be(WorkflowStatus.Validated);

        workflow.Reserve();
        workflow.Status.Should().Be(WorkflowStatus.Reserved);

        workflow.StartProcessing();
        workflow.Status.Should().Be(WorkflowStatus.Processing);

        workflow.AwaitCallback("CALLBACK-REF-999");
        workflow.Status.Should().Be(WorkflowStatus.WaitingCallback);

        workflow.Complete();
        workflow.Status.Should().Be(WorkflowStatus.Completed);
        workflow.DomainEvents.Should().Contain(e => e is WorkflowCompletedEvent);
    }

    [Fact]
    public void IllegalTransition_CreatedToCompleted_ShouldThrowInvariantException()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("CORR-ILLEGAL", "{}");

        // Act
        Action act = () => workflow.Complete();

        // Assert
        act.Should().Throw<WorkflowInvariantException>()
           .WithMessage("*Illegal State Transition*Cannot transition from Created to Completed*");
    }

    [Fact]
    public void TerminalState_Completed_CannotUndergoFurtherTransitions()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("CORR-TERMINAL", "{}");
        workflow.Validate();
        workflow.Reserve();
        workflow.StartProcessing();
        workflow.Complete();

        // Act
        Action act = () => workflow.Validate();

        // Assert
        act.Should().Throw<WorkflowInvariantException>()
           .WithMessage("*TerminalStateImmutability*");
    }

    [Fact]
    public void TerminalState_Failed_CannotTransitionToCompleted()
    {
        // Arrange
        var workflow = WorkflowInstance.Create("CORR-FAIL-TERM", "{}");
        workflow.Fail("Critical external provider timeout");

        // Act
        Action act = () => workflow.Complete();

        // Assert
        act.Should().Throw<WorkflowInvariantException>()
           .WithMessage("*TerminalStateImmutability*");
    }

    [Theory]
    [InlineData("provider said \"declined\"")]
    [InlineData("first line\nsecond line")]
    public void Fail_WithSpecialCharacters_StoresValidJson(string reason)
    {
        var workflow = WorkflowInstance.Create("CORR-FAIL-JSON", "{}");

        workflow.Fail(reason);

        using var details = JsonDocument.Parse(workflow.ErrorDetailsJson!);
        details.RootElement.GetProperty("reason").GetString().Should().Be(reason);
    }

    [Theory]
    [InlineData("user requested \"stop\"")]
    [InlineData("first line\nsecond line")]
    public void Cancel_WithSpecialCharacters_StoresValidJson(string reason)
    {
        var workflow = WorkflowInstance.Create("CORR-CANCEL-JSON", "{}");

        workflow.Cancel(reason);

        using var details = JsonDocument.Parse(workflow.ErrorDetailsJson!);
        details.RootElement.GetProperty("cancellation_reason").GetString().Should().Be(reason);
    }
}
