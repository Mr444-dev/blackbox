using System.Security.Cryptography;
using System.Text;
using Blackbox.Core.Cryptography;
using Blackbox.Infrastructure.Cryptography;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class MerkleAuditTests
{
    [Fact]
    public void MerkleTree_DeterministicRoot_ShouldProduceConsistentRootForIdenticalLeaves()
    {
        var leavesA = new List<string>
        {
            "hash_leaf_001",
            "hash_leaf_002",
            "hash_leaf_003",
            "hash_leaf_004"
        };

        var leavesB = new List<string>(leavesA);

        var treeA = new MerkleTree(leavesA);
        var treeB = new MerkleTree(leavesB);

        treeA.Root.Should().NotBeNullOrWhiteSpace();
        treeA.Root.Should().Be(treeB.Root);
    }

    [Fact]
    public void MerkleTree_GenerateProof_ShouldVerifySuccessfullyForAnyLeafIndex()
    {
        var leaves = new List<string>
        {
            "event_alpha",
            "event_beta",
            "event_gamma",
            "event_delta",
            "event_epsilon" // Odd number of leaves tests duplication invariant
        };

        var tree = new MerkleTree(leaves);

        for (int i = 0; i < leaves.Count; i++)
        {
            var proof = tree.GenerateProof(i);
            proof.RootHash.Should().Be(tree.Root);
            proof.LeafHash.Should().Be(leaves[i]);

            // Verification using raw leaf hash
            var isValid = proof.VerifyLeafHash(tree.Root, leaves[i]);
            isValid.Should().BeTrue($"Proof for leaf {i} must cryptographically verify against root");
        }
    }

    [Fact]
    public void MerkleTree_ProofTampering_BitFlipShouldCauseVerificationToFail()
    {
        var leaves = new List<string> { "h1", "h2", "h3", "h4" };
        var tree = new MerkleTree(leaves);

        var proof = tree.GenerateProof(2);

        // 1. Bit-flip in the leaf hash itself
        var tamperedLeaf = "h3_tampered";
        proof.VerifyLeafHash(tree.Root, tamperedLeaf).Should().BeFalse();

        // 2. Tampered root hash
        var fakeRoot = "0000000000000000000000000000000000000000000000000000000000000000";
        proof.VerifyLeafHash(fakeRoot, leaves[2]).Should().BeFalse();
    }

    [Fact]
    public void MerkleAuditLedger_AppendAndSeal_ShouldCreateImmutableVerifiableChain()
    {
        var ledger = new MerkleAuditLedger();
        var workflowId = Guid.NewGuid();

        var entry1 = ledger.AppendEvent(workflowId, "CREATED", "VALIDATED", "{\"step\":1}");
        var entry2 = ledger.AppendEvent(workflowId, "VALIDATED", "RESERVED", "{\"step\":2}");
        var entry3 = ledger.AppendEvent(workflowId, "RESERVED", "COMPLETED", "{\"step\":3}");

        // Assert Hash Chain link: entry2 must link to entry1, entry3 to entry2
        entry2.PreviousEntryHash.Should().Be(entry1.EntryHash);
        entry3.PreviousEntryHash.Should().Be(entry2.EntryHash);

        // Seal block
        var block = ledger.SealBlock();
        block.LeafCount.Should().Be(3);
        block.MerkleRoot.Should().NotBeNullOrWhiteSpace();

        // Obtain proof for entry2
        var proof = ledger.GenerateProof(entry2.EntryHash);
        proof.Should().NotBeNull();

        // Verify proof with ledger
        ledger.VerifyProof(proof!).Should().BeTrue();
    }

    [Fact]
    public void ZkAuditVerifier_ZeroKnowledgeClaim_ShouldVerifyWithoutDisclosingRawPayload()
    {
        var ledger = new MerkleAuditLedger();
        var workflowId = Guid.NewGuid();
        var confidentialPayload = "{\"creditCard\":\"4111-2222-3333-4444\",\"ssn\":\"123-45-6789\"}";

        // 1. Ledger commits the event
        var entry = ledger.AppendEvent(workflowId, "PROCESSING", "COMPLETED", confidentialPayload);
        var block = ledger.SealBlock();
        var proof = ledger.GenerateProof(entry.EntryHash)!;

        // 2. Auditor receives only the public claim + hashed payload commitment
        var publicClaim = new ZkAuditPublicClaim(
            entry.SequenceNumber,
            entry.WorkflowId,
            entry.FromState,
            entry.ToState,
            entry.TimestampUtc,
            entry.PayloadHash, // Cryptographic hash commitment only
            entry.PreviousEntryHash);

        // 3. Auditor verifies statement without ever receiving the raw confidential payload
        var isZkValid = ZkAuditVerifier.VerifyClaimWithoutPayload(block.MerkleRoot, publicClaim, proof);
        isZkValid.Should().BeTrue("Auditor must be able to verify transition correctness with zero knowledge of PII payload");

        // 4. Assert tampering with state transition fails
        var fakeClaim = publicClaim with { ToState = "FAILED" };
        var isFakeValid = ZkAuditVerifier.VerifyClaimWithoutPayload(block.MerkleRoot, fakeClaim, proof);
        isFakeValid.Should().BeFalse("Tampered state transition claim must be rejected");
    }
}
