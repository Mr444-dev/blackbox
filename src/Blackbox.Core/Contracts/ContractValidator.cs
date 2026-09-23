using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blackbox.Core.Contracts;

public class BreakingContractException : Exception
{
    public string SchemaName { get; }
    public string ExpectedVersion { get; }

    public BreakingContractException(string schemaName, string expectedVersion, string message)
        : base($"[Breaking Contract Drift Detected in '{schemaName}:{expectedVersion}'] {message}")
    {
        SchemaName = schemaName;
        ExpectedVersion = expectedVersion;
    }
}

public interface IContractValidator
{
    void ValidateEventPayload(string eventType, string payloadJson);
}

public class SchemaContractValidator : IContractValidator
{
    // Dictionary of event types to their required properties and expected JSON value kinds
    private static readonly Dictionary<string, Dictionary<string, JsonValueKind>> _registeredContracts = new()
    {
        ["WorkflowCreated.v1"] = new Dictionary<string, JsonValueKind>
        {
            ["WorkflowId"] = JsonValueKind.String,
            ["CorrelationId"] = JsonValueKind.String,
            ["PayloadJson"] = JsonValueKind.String,
            ["EventId"] = JsonValueKind.String,
            ["OccurredOnUtc"] = JsonValueKind.String
        },
        ["ResourceReserved.v1"] = new Dictionary<string, JsonValueKind>
        {
            ["WorkflowId"] = JsonValueKind.String,
            ["ResourceId"] = JsonValueKind.String,
            ["Quantity"] = JsonValueKind.Number,
            ["EventId"] = JsonValueKind.String,
            ["OccurredOnUtc"] = JsonValueKind.String
        },
        ["WorkflowCompleted.v1"] = new Dictionary<string, JsonValueKind>
        {
            ["WorkflowId"] = JsonValueKind.String,
            ["CorrelationId"] = JsonValueKind.String,
            ["CompletedAtUtc"] = JsonValueKind.String,
            ["EventId"] = JsonValueKind.String,
            ["OccurredOnUtc"] = JsonValueKind.String
        }
    };

    public void ValidateEventPayload(string eventType, string payloadJson)
    {
        if (!_registeredContracts.TryGetValue(eventType, out var requiredContract))
        {
            // Unknown contract version or unregistered
            return;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new BreakingContractException(eventType, "current", $"Malformed JSON payload: {ex.Message}");
        }

        if (rootNode is not JsonObject jsonObject)
        {
            throw new BreakingContractException(eventType, "current", "Payload root must be a JSON object.");
        }

        foreach (var (expectedField, expectedKind) in requiredContract)
        {
            if (!jsonObject.TryGetPropertyValue(expectedField, out var propertyNode) || propertyNode == null)
            {
                throw new BreakingContractException(
                    eventType,
                    "current",
                    $"Missing mandatory contract property '{expectedField}'. Downstream consumers would fail.");
            }

            var actualKind = propertyNode.GetValue<JsonElement>().ValueKind;
            if (actualKind != expectedKind)
            {
                throw new BreakingContractException(
                    eventType,
                    "current",
                    $"Type mismatch for property '{expectedField}'. Expected '{expectedKind}', received '{actualKind}'.");
            }
        }
    }
}
