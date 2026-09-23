using Blackbox.Core.Contracts;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class ContractValidationTests
{
    private readonly SchemaContractValidator _validator = new();

    [Fact]
    public void ValidateEventPayload_ValidResourceReservedPayload_ShouldPassWithoutException()
    {
        // Arrange
        var payload = """
        {
            "WorkflowId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "ResourceId": "SKU-LAPTOP-001",
            "Quantity": 2,
            "EventId": "e1f13f64-5717-4562-b3fc-2c963f66afa6",
            "OccurredOnUtc": "2026-09-06T10:00:00Z"
        }
        """;

        // Act
        Action act = () => _validator.ValidateEventPayload("ResourceReserved.v1", payload);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateEventPayload_MissingRequiredField_ShouldThrowBreakingContractException()
    {
        // Arrange: Missing "Quantity"
        var brokenPayload = """
        {
            "WorkflowId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "ResourceId": "SKU-LAPTOP-001",
            "EventId": "e1f13f64-5717-4562-b3fc-2c963f66afa6",
            "OccurredOnUtc": "2026-09-06T10:00:00Z"
        }
        """;

        // Act
        Action act = () => _validator.ValidateEventPayload("ResourceReserved.v1", brokenPayload);

        // Assert
        act.Should().Throw<BreakingContractException>()
            .WithMessage("*Missing mandatory contract property 'Quantity'*");
    }

    [Fact]
    public void ValidateEventPayload_TypeMismatch_ShouldThrowBreakingContractException()
    {
        // Arrange: "Quantity" is passed as a string "two" instead of number
        var brokenPayload = """
        {
            "WorkflowId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "ResourceId": "SKU-LAPTOP-001",
            "Quantity": "two",
            "EventId": "e1f13f64-5717-4562-b3fc-2c963f66afa6",
            "OccurredOnUtc": "2026-09-06T10:00:00Z"
        }
        """;

        // Act
        Action act = () => _validator.ValidateEventPayload("ResourceReserved.v1", brokenPayload);

        // Assert
        act.Should().Throw<BreakingContractException>()
            .WithMessage("*Type mismatch for property 'Quantity'*Expected 'Number', received 'String'*");
    }
}
