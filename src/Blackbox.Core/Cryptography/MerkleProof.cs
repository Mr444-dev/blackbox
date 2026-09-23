using System.Security.Cryptography;
using System.Text;

namespace Blackbox.Core.Cryptography;

public enum ProofDirection
{
    Left,
    Right
}

public record MerkleProofStep(string SiblingHash, ProofDirection Direction);

public class MerkleProof
{
    public string LeafHash { get; init; }
    public int LeafIndex { get; init; }
    public IReadOnlyList<MerkleProofStep> Steps { get; init; }
    public string RootHash { get; init; }

    public MerkleProof(string leafHash, int leafIndex, IReadOnlyList<MerkleProofStep> steps, string rootHash)
    {
        LeafHash = leafHash;
        LeafIndex = leafIndex;
        Steps = steps;
        RootHash = rootHash;
    }

    // Parameterless constructor for deserialization
    public MerkleProof()
    {
        LeafHash = string.Empty;
        Steps = Array.Empty<MerkleProofStep>();
        RootHash = string.Empty;
    }

    public bool Verify(string expectedRoot, string rawLeafData)
    {
        using var sha256 = SHA256.Create();
        var computedLeafHash = Convert.ToHexStringLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawLeafData)));
        return VerifyLeafHash(expectedRoot, computedLeafHash);
    }

    public bool VerifyLeafHash(string expectedRoot, string computedLeafHash)
    {
        if (!string.Equals(computedLeafHash, LeafHash, StringComparison.OrdinalIgnoreCase))
            return false;

        var currentHash = computedLeafHash;
        using var sha256 = SHA256.Create();

        foreach (var step in Steps)
        {
            string combined;
            if (step.Direction == ProofDirection.Left)
            {
                // Sibling is on the left: Hash(Sibling + Current)
                combined = step.SiblingHash + currentHash;
            }
            else
            {
                // Sibling is on the right: Hash(Current + Sibling)
                combined = currentHash + step.SiblingHash;
            }

            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
            currentHash = Convert.ToHexStringLower(hashBytes);
        }

        return string.Equals(currentHash, expectedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
