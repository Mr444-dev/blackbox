using System.Text.Json;

namespace Blackbox.Core.Domain;

/// <summary>
/// Compares workflow payloads as JSON values instead of comparing their source
/// text. PostgreSQL jsonb normalizes whitespace and object-key order on storage.
/// </summary>
public static class WorkflowPayloadComparer
{
    public static bool AreEquivalent(string leftJson, string rightJson)
    {
        try
        {
            using var left = JsonDocument.Parse(leftJson);
            using var right = JsonDocument.Parse(rightJson);
            return JsonElement.DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
