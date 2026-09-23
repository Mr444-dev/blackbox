using System.Text.Json;

namespace Blackbox.Core.Contracts;

public enum CompatibilityStatus
{
    Compatible,
    Breaking,
    Unknown
}

public record SchemaField(string Name, string Type, bool IsRequired);

public record SchemaVersionDefinition(
    string SchemaName,
    string Version,
    Dictionary<string, SchemaField> Fields);

public record DriftMatrixCell(
    string ProducerVersion,
    string ConsumerVersion,
    CompatibilityStatus Status,
    string Reason);

public record DriftMatrixReport(
    string SchemaName,
    List<DriftMatrixCell> Cells,
    string MarkdownTable,
    bool HasBreakingChanges);

public class ContractDriftMatrixGenerator
{
    public DriftMatrixReport GenerateDriftMatrix(
        string schemaName,
        IReadOnlyList<SchemaVersionDefinition> versions)
    {
        var cells = new List<DriftMatrixCell>();

        var sortedVersions = versions.OrderBy(v => v.Version).ToList();

        foreach (var producer in sortedVersions)
        {
            foreach (var consumer in sortedVersions)
            {
                var (status, reason) = EvaluateCompatibility(producer, consumer);
                cells.Add(new DriftMatrixCell(producer.Version, consumer.Version, status, reason));
            }
        }

        // Build Markdown Table
        var versionNames = sortedVersions.Select(v => v.Version).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### 📋 Contract Compatibility Matrix: `{schemaName}`\n");
        sb.AppendLine("| Producer \\ Consumer | " + string.Join(" | ", versionNames.Select(v => $"Consumer `{v}`")) + " |");
        sb.AppendLine("| :--- | " + string.Join(" | ", versionNames.Select(_ => ":---:")) + " |");

        foreach (var p in versionNames)
        {
            var rowValues = new List<string>();
            foreach (var c in versionNames)
            {
                var cell = cells.First(x => x.ProducerVersion == p && x.ConsumerVersion == c);
                string icon = cell.Status switch
                {
                    CompatibilityStatus.Compatible => "✅ COMPATIBLE",
                    CompatibilityStatus.Breaking => "❌ BREAKING",
                    _ => "⚠️ UNKNOWN"
                };
                rowValues.Add(icon);
            }
            sb.AppendLine($"| **Producer `{p}`** | " + string.Join(" | ", rowValues) + " |");
        }

        bool hasBreaking = cells.Any(c => c.Status == CompatibilityStatus.Breaking);

        return new DriftMatrixReport(
            SchemaName: schemaName,
            Cells: cells,
            MarkdownTable: sb.ToString(),
            HasBreakingChanges: hasBreaking);
    }

    private static (CompatibilityStatus Status, string Reason) EvaluateCompatibility(
        SchemaVersionDefinition producer,
        SchemaVersionDefinition consumer)
    {
        // A producer is compatible with a consumer IF the producer supplies
        // ALL fields that the consumer considers required, with identical or wider types.
        var missingRequired = new List<string>();
        var typeMismatches = new List<string>();

        foreach (var (consumerField, consumerMeta) in consumer.Fields)
        {
            if (!consumerMeta.IsRequired)
                continue;

            if (!producer.Fields.TryGetValue(consumerField, out var producerMeta))
            {
                missingRequired.Add(consumerField);
            }
            else if (producerMeta.Type != consumerMeta.Type)
            {
                typeMismatches.Add($"{consumerField} (Expected: {consumerMeta.Type}, Provided: {producerMeta.Type})");
            }
        }

        if (missingRequired.Count > 0)
        {
            return (CompatibilityStatus.Breaking, $"Producer `{producer.Version}` missing required consumer `{consumer.Version}` fields: {string.Join(", ", missingRequired)}");
        }

        if (typeMismatches.Count > 0)
        {
            return (CompatibilityStatus.Breaking, $"Type mismatch: {string.Join(", ", typeMismatches)}");
        }

        return (CompatibilityStatus.Compatible, "All required fields and types match consumer expectations.");
    }
}
