using System.Security.Cryptography;
using System.Text;

namespace Blackbox.Core.Cryptography;

public class MerkleTree
{
    private readonly List<List<string>> _layers = new();

    public IReadOnlyList<string> Leaves => _layers.Count > 0 ? _layers[0] : Array.Empty<string>();
    public string Root => _layers.Count > 0 && _layers[^1].Count > 0 ? _layers[^1][0] : string.Empty;

    public MerkleTree(IEnumerable<string> leafHashes)
    {
        var currentLayer = leafHashes.ToList();
        if (currentLayer.Count == 0)
        {
            throw new ArgumentException("Cannot build a Merkle Tree with zero leaves.", nameof(leafHashes));
        }

        _layers.Add(new List<string>(currentLayer));
        BuildTree();
    }

    private void BuildTree()
    {
        using var sha256 = SHA256.Create();

        while (_layers[^1].Count > 1)
        {
            var currentLayer = _layers[^1];
            var nextLayer = new List<string>();

            for (int i = 0; i < currentLayer.Count; i += 2)
            {
                var left = currentLayer[i];
                // If odd number of nodes, duplicate the last node (Bitcoin/RFC 6962 standard)
                var right = (i + 1 < currentLayer.Count) ? currentLayer[i + 1] : left;

                var combined = left + right;
                var parentHash = Convert.ToHexStringLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(combined)));
                nextLayer.Add(parentHash);
            }

            _layers.Add(nextLayer);
        }
    }

    public MerkleProof GenerateProof(int leafIndex)
    {
        if (leafIndex < 0 || leafIndex >= _layers[0].Count)
            throw new ArgumentOutOfRangeException(nameof(leafIndex), "Leaf index out of bounds.");

        var proofSteps = new List<MerkleProofStep>();
        var currentIndex = leafIndex;

        for (int layerIndex = 0; layerIndex < _layers.Count - 1; layerIndex++)
        {
            var currentLayer = _layers[layerIndex];
            bool isRightNode = (currentIndex % 2 == 1);
            int siblingIndex = isRightNode ? currentIndex - 1 : currentIndex + 1;

            if (siblingIndex < currentLayer.Count)
            {
                var siblingHash = currentLayer[siblingIndex];
                var direction = isRightNode ? ProofDirection.Left : ProofDirection.Right;
                proofSteps.Add(new MerkleProofStep(siblingHash, direction));
            }
            else
            {
                // Odd node paired with duplicate of itself
                var siblingHash = currentLayer[currentIndex];
                proofSteps.Add(new MerkleProofStep(siblingHash, ProofDirection.Right));
            }

            currentIndex /= 2;
        }

        return new MerkleProof(_layers[0][leafIndex], leafIndex, proofSteps, Root);
    }
}
