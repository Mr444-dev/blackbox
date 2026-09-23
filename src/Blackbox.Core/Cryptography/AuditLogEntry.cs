using System.Security.Cryptography;
using System.Text;

namespace Blackbox.Core.Cryptography;

public class AuditLogEntry
{
    public long SequenceNumber { get; init; }
    public Guid WorkflowId { get; init; }
    public string FromState { get; init; }
    public string ToState { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string PayloadHash { get; init; }
    public string PreviousEntryHash { get; init; }
    public string EntryHash { get; init; }

    public AuditLogEntry(
        long sequenceNumber,
        Guid workflowId,
        string fromState,
        string toState,
        DateTime timestampUtc,
        string payloadHash,
        string previousEntryHash)
    {
        SequenceNumber = sequenceNumber;
        WorkflowId = workflowId;
        FromState = fromState;
        ToState = toState;
        TimestampUtc = timestampUtc;
        PayloadHash = payloadHash;
        PreviousEntryHash = previousEntryHash;
        EntryHash = ComputeHash();
    }

    // Parameterless constructor for deserialization
    public AuditLogEntry()
    {
        FromState = string.Empty;
        ToState = string.Empty;
        PayloadHash = string.Empty;
        PreviousEntryHash = string.Empty;
        EntryHash = string.Empty;
    }

    public string ComputeHash()
    {
        var rawData = $"{SequenceNumber}:{WorkflowId}:{FromState}:{ToState}:{TimestampUtc.Ticks}:{PayloadHash}:{PreviousEntryHash}";
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexStringLower(bytes);
    }

    public bool VerifyHash()
    {
        return string.Equals(EntryHash, ComputeHash(), StringComparison.OrdinalIgnoreCase);
    }

    public static string HashPayload(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));

        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexStringLower(bytes);
    }
}
