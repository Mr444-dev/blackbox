using System.Collections.Concurrent;
using Blackbox.Core.Cryptography;

namespace Blackbox.Infrastructure.Cryptography;

public record MerkleBlock(
    Guid Id,
    long BlockNumber,
    string MerkleRoot,
    int LeafCount,
    DateTime SealedAt,
    IReadOnlyList<string> EntryHashes);

public interface IAuditLedger
{
    AuditLogEntry AppendEvent(Guid workflowId, string fromState, string toState, string payloadJson);
    MerkleBlock SealBlock();
    MerkleProof? GenerateProof(string entryHash);
    bool VerifyProof(MerkleProof proof);
    IReadOnlyList<MerkleBlock> GetSealedBlocks();
    IReadOnlyList<AuditLogEntry> GetUnsealedEntries();
    AuditLogEntry? GetEntry(string entryHash);
}

public class MerkleAuditLedger : IAuditLedger
{
    private readonly object _lock = new();
    private readonly List<AuditLogEntry> _unsealedEntries = new();
    private readonly List<MerkleBlock> _sealedBlocks = new();
    private readonly ConcurrentDictionary<string, AuditLogEntry> _entriesByHash = new();
    private readonly ConcurrentDictionary<string, MerkleProof> _proofsByHash = new();

    private long _sequenceCounter = 0;
    private long _blockCounter = 0;
    private string _lastEntryHash = "0000000000000000000000000000000000000000000000000000000000000000"; // Genesis Hash

    public AuditLogEntry AppendEvent(Guid workflowId, string fromState, string toState, string payloadJson)
    {
        lock (_lock)
        {
            _sequenceCounter++;
            var payloadHash = AuditLogEntry.HashPayload(payloadJson);
            var entry = new AuditLogEntry(
                sequenceNumber: _sequenceCounter,
                workflowId: workflowId,
                fromState: fromState,
                toState: toState,
                timestampUtc: DateTime.UtcNow,
                payloadHash: payloadHash,
                previousEntryHash: _lastEntryHash);

            _lastEntryHash = entry.EntryHash;
            _unsealedEntries.Add(entry);
            _entriesByHash[entry.EntryHash] = entry;

            return entry;
        }
    }

    public MerkleBlock SealBlock()
    {
        lock (_lock)
        {
            if (_unsealedEntries.Count == 0)
            {
                throw new InvalidOperationException("Cannot seal an empty audit block. Append events first.");
            }

            _blockCounter++;
            var entryHashes = _unsealedEntries.Select(e => e.EntryHash).ToList();
            var tree = new MerkleTree(entryHashes);

            // Pre-generate and cache proofs for all entries sealed in this block
            for (int i = 0; i < entryHashes.Count; i++)
            {
                var proof = tree.GenerateProof(i);
                _proofsByHash[entryHashes[i]] = proof;
            }

            var block = new MerkleBlock(
                Id: Guid.NewGuid(),
                BlockNumber: _blockCounter,
                MerkleRoot: tree.Root,
                LeafCount: entryHashes.Count,
                SealedAt: DateTime.UtcNow,
                EntryHashes: entryHashes);

            _sealedBlocks.Add(block);
            _unsealedEntries.Clear();

            return block;
        }
    }

    public MerkleProof? GenerateProof(string entryHash)
    {
        _proofsByHash.TryGetValue(entryHash, out var proof);
        return proof;
    }

    public bool VerifyProof(MerkleProof proof)
    {
        if (string.IsNullOrEmpty(proof.RootHash) || string.IsNullOrEmpty(proof.LeafHash))
            return false;

        // Check if RootHash corresponds to one of our certified sealed blocks
        var isCertifiedBlock = _sealedBlocks.Any(b => string.Equals(b.MerkleRoot, proof.RootHash, StringComparison.OrdinalIgnoreCase));
        if (!isCertifiedBlock)
            return false;

        return proof.VerifyLeafHash(proof.RootHash, proof.LeafHash);
    }

    public IReadOnlyList<MerkleBlock> GetSealedBlocks()
    {
        lock (_lock)
        {
            return _sealedBlocks.ToList();
        }
    }

    public IReadOnlyList<AuditLogEntry> GetUnsealedEntries()
    {
        lock (_lock)
        {
            return _unsealedEntries.ToList();
        }
    }

    public AuditLogEntry? GetEntry(string entryHash)
    {
        _entriesByHash.TryGetValue(entryHash, out var entry);
        return entry;
    }
}
