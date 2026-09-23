using System.Text;
using Blackbox.Core.Cryptography.PostQuantum;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class PostQuantumCryptographyTests
{
    private readonly PostQuantumDilithiumSigner _dilithiumSigner = new();
    private readonly PostQuantumKyberKEM _kyberKem = new();
    private readonly QuantumResistantAuditSealer _auditSealer = new();

    [Fact]
    public void Dilithium_SignAndVerify_ValidPayload_ReturnsTrue()
    {
        // Arrange: Generate NIST FIPS 204 ML-DSA Dilithium keypair
        var keyPair = _dilithiumSigner.GenerateKeyPair();
        byte[] auditPayload = Encoding.UTF8.GetBytes("WORKFLOW-AUDIT-MERKLE-ROOT:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        // Act: Sign payload using lattice vectors
        var signature = _dilithiumSigner.Sign(auditPayload, keyPair.PrivateKey);

        // Assert
        signature.Should().NotBeNull();
        signature.Algorithm.Should().Be("NIST-FIPS-204-ML-DSA-Dilithium3");
        signature.SecurityCategory.Should().Contain("Quantum-Resistant");

        // Verify with matching public key
        bool isValid = _dilithiumSigner.Verify(auditPayload, signature, keyPair.PublicKey);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Dilithium_WhenMessageIsTampered_ReturnsFalse()
    {
        // Arrange
        var keyPair = _dilithiumSigner.GenerateKeyPair();
        byte[] originalPayload = Encoding.UTF8.GetBytes("MERKLE-ROOT-BEFORE-TAMPERING");
        byte[] tamperedPayload = Encoding.UTF8.GetBytes("MERKLE-ROOT-AFTER-TAMPERING");

        // Act
        var signature = _dilithiumSigner.Sign(originalPayload, keyPair.PrivateKey);

        // Assert
        bool isValid = _dilithiumSigner.Verify(tamperedPayload, signature, keyPair.PublicKey);
        isValid.Should().BeFalse();
    }

    [Fact]
    public void Dilithium_WhenWrongPublicKeyProvided_ReturnsFalse()
    {
        // Arrange
        var keyPairA = _dilithiumSigner.GenerateKeyPair();
        var keyPairB = _dilithiumSigner.GenerateKeyPair();
        byte[] payload = Encoding.UTF8.GetBytes("CRITICAL-FINANCIAL-WORKFLOW-SEAL");

        // Act
        var signature = _dilithiumSigner.Sign(payload, keyPairA.PrivateKey);

        // Assert: Verify with unrelated public key B
        bool isValid = _dilithiumSigner.Verify(payload, signature, keyPairB.PublicKey);
        isValid.Should().BeFalse();
    }

    [Fact]
    public void Kyber_EncapsulateAndDecapsulate_DerivesIdenticalSharedSecret()
    {
        // Arrange: Generate NIST FIPS 203 ML-KEM Kyber keypair
        var keyPair = _kyberKem.GenerateKeyPair();

        // Act 1: Sender encapsulates symmetric key using recipient's public key
        var encapsulation = _kyberKem.Encapsulate(keyPair.PublicKey);

        // Act 2: Recipient decapsulates symmetric key using secret key
        byte[] recipientSharedSecret = _kyberKem.Decapsulate(encapsulation.Ciphertext, keyPair.PrivateKey);

        // Assert: Both parties derived the exact same 256-bit post-quantum symmetric key
        encapsulation.SharedSecret256.Should().NotBeNull();
        encapsulation.SharedSecret256.Length.Should().Be(32);
        recipientSharedSecret.Should().Equal(encapsulation.SharedSecret256);
    }

    [Fact]
    public void QuantumAuditSealer_SealAndVerifyMerkleRoot_ValidSeal_ReturnsTrue()
    {
        // Arrange
        var keyPair = _dilithiumSigner.GenerateKeyPair();
        var workflowId = Guid.NewGuid();
        string merkleRootHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

        // Act
        var seal = _auditSealer.CreateSeal(workflowId, merkleRootHash, keyPair);

        // Assert Seal Details
        seal.Should().NotBeNull();
        seal.WorkflowId.Should().Be(workflowId);
        seal.MerkleRootHash.Should().Be(merkleRootHash);
        seal.Algorithm.Should().Be("NIST-FIPS-204-ML-DSA-Dilithium3");

        // Verify valid seal
        bool isValid = _auditSealer.VerifySeal(seal);
        isValid.Should().BeTrue();

        // Verify tampered seal fails
        var tamperedSeal = seal with { MerkleRootHash = "0000000000000000000000000000000000000000000000000000000000000000" };
        bool isTamperedValid = _auditSealer.VerifySeal(tamperedSeal);
        isTamperedValid.Should().BeFalse();
    }
}
