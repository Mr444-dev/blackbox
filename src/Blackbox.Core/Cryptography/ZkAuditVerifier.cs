namespace Blackbox.Core.Cryptography;

public record ZkAuditPublicClaim(
    long SequenceNumber,
    Guid WorkflowId,
    string FromState,
    string ToState,
    DateTime TimestampUtc,
    string PayloadHash,
    string PreviousEntryHash);

public static class ZkAuditVerifier
{
    /// <summary>
    /// Verifies that a business workflow event was executed and permanently sealed into the
    /// certified Merkle Root without exposing or disclosing the confidential transaction payload.
    /// </summary>
    public static bool VerifyClaimWithoutPayload(
        string certifiedMerkleRoot,
        ZkAuditPublicClaim claim,
        MerkleProof proof)
    {
        // 1. Recompute the deterministic Entry Hash using the public claim fields + committed payload hash
        var entry = new AuditLogEntry(
            claim.SequenceNumber,
            claim.WorkflowId,
            claim.FromState,
            claim.ToState,
            claim.TimestampUtc,
            claim.PayloadHash,
            claim.PreviousEntryHash);

        var computedEntryHash = entry.ComputeHash();

        // 2. Verify that the entry hash matches the leaf hash in the proof
        if (!string.Equals(computedEntryHash, proof.LeafHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 3. Cryptographically verify the Merkle inclusion path against the certified root
        return proof.VerifyLeafHash(certifiedMerkleRoot, computedEntryHash);
    }
}
