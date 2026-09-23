using Blackbox.Core.Contracts;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class ContractDriftMatrixTests
{
    [Fact]
    public void GenerateDriftMatrix_WithThreeVersions_AccuratelyIdentifiesCompatibleAndBreakingPairings()
    {
        // Arrange
        var generator = new ContractDriftMatrixGenerator();

        var v1 = new SchemaVersionDefinition("OrderCreatedEvent", "v1", new Dictionary<string, SchemaField>
        {
            ["orderId"] = new("orderId", "string", true),
            ["customerId"] = new("customerId", "string", true),
            ["amount"] = new("amount", "number", true)
        });

        var v2 = new SchemaVersionDefinition("OrderCreatedEvent", "v2", new Dictionary<string, SchemaField>
        {
            ["orderId"] = new("orderId", "string", true),
            ["customerId"] = new("customerId", "string", true),
            ["amount"] = new("amount", "number", true),
            ["currency"] = new("currency", "string", false) // Optional new field
        });

        var v3 = new SchemaVersionDefinition("OrderCreatedEvent", "v3", new Dictionary<string, SchemaField>
        {
            ["orderId"] = new("orderId", "string", true),
            // customerId was REMOVED! (Breaking change)
            ["amount"] = new("amount", "number", true),
            ["currency"] = new("currency", "string", true)
        });

        // Act
        var report = generator.GenerateDriftMatrix("OrderCreatedEvent", new[] { v1, v2, v3 });

        // Assert
        report.Should().NotBeNull();
        report.Cells.Should().HaveCount(9);
        report.HasBreakingChanges.Should().BeTrue();

        // v1 producer -> v1 consumer: Compatible
        report.Cells.First(c => c.ProducerVersion == "v1" && c.ConsumerVersion == "v1").Status.Should().Be(CompatibilityStatus.Compatible);

        // v2 producer -> v1 consumer: Compatible
        report.Cells.First(c => c.ProducerVersion == "v2" && c.ConsumerVersion == "v1").Status.Should().Be(CompatibilityStatus.Compatible);

        // v3 producer -> v1 consumer: BREAKING (v1 consumer requires customerId which v3 removed)
        var v3ToV1 = report.Cells.First(c => c.ProducerVersion == "v3" && c.ConsumerVersion == "v1");
        v3ToV1.Status.Should().Be(CompatibilityStatus.Breaking);
        v3ToV1.Reason.Should().Contain("customerId");

        // v3 producer -> v2 consumer: BREAKING
        report.Cells.First(c => c.ProducerVersion == "v3" && c.ConsumerVersion == "v2").Status.Should().Be(CompatibilityStatus.Breaking);

        // Markdown table rendering
        report.MarkdownTable.Should().Contain("✅ COMPATIBLE");
        report.MarkdownTable.Should().Contain("❌ BREAKING");
    }
}
