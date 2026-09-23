using System.Text;

namespace Blackbox.Core.Cryptography.PostQuantum;

public record QuantumAuditSeal(
    Guid SealId,
    Guid WorkflowId,
    string MerkleRootHash,
    string Algorithm,
    string SignerKeyId,
    string PublicKeySeedRhoHex,
    string PublicKeyTHex,
    string CommitmentHashHex,
    string SignatureVectorZHex,
    DateTime TimestampUtc,
    string SecurityCategory);

public interface IQuantumAuditSealer
{
    QuantumAuditSeal CreateSeal(Guid workflowId, string merkleRootHash, DilithiumKeyPair keyPair);
    bool VerifySeal(QuantumAuditSeal seal);
}

public class QuantumResistantAuditSealer : IQuantumAuditSealer
{
    private readonly PostQuantumDilithiumSigner _signer = new();

    public QuantumAuditSeal CreateSeal(Guid workflowId, string merkleRootHash, DilithiumKeyPair keyPair)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"{workflowId:N}:{merkleRootHash}");
        var signature = _signer.Sign(payload, keyPair.PrivateKey);

        byte[] tBytes = PostQuantumDilithiumSigner.SerializeVector(keyPair.PublicKey.VectorT);
        byte[] zBytes = PostQuantumDilithiumSigner.SerializeVector(signature.SignatureVectorZ);

        return new QuantumAuditSeal(
            SealId: Guid.NewGuid(),
            WorkflowId: workflowId,
            MerkleRootHash: merkleRootHash,
            Algorithm: signature.Algorithm,
            SignerKeyId: signature.KeyId,
            PublicKeySeedRhoHex: Convert.ToHexString(keyPair.PublicKey.SeedRho).ToLowerInvariant(),
            PublicKeyTHex: Convert.ToHexString(tBytes).ToLowerInvariant(),
            CommitmentHashHex: Convert.ToHexString(signature.CommitmentHash).ToLowerInvariant(),
            SignatureVectorZHex: Convert.ToHexString(zBytes).ToLowerInvariant(),
            TimestampUtc: DateTime.UtcNow,
            SecurityCategory: signature.SecurityCategory);
    }

    public bool VerifySeal(QuantumAuditSeal seal)
    {
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes($"{seal.WorkflowId:N}:{seal.MerkleRootHash}");

            int[] tVec = PostQuantumDilithiumSigner.DeserializeVector(Convert.FromHexString(seal.PublicKeyTHex));
            int[] zVec = PostQuantumDilithiumSigner.DeserializeVector(Convert.FromHexString(seal.SignatureVectorZHex));

            var pubKey = new DilithiumPublicKey(
                seal.Algorithm,
                seal.SignerKeyId,
                Convert.FromHexString(seal.PublicKeySeedRhoHex),
                tVec);

            var sig = new DilithiumSignature(
                seal.Algorithm,
                seal.SignerKeyId,
                Convert.FromHexString(seal.CommitmentHashHex),
                zVec,
                seal.SecurityCategory);

            return _signer.Verify(payload, sig, pubKey);
        }
        catch
        {
            return false;
        }
    }
}
