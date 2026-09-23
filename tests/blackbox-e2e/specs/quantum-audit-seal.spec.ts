import { test, expect } from '@playwright/test';

test.describe('NIST Post-Quantum Cryptography & Quantum-Resistant Audit Trail', () => {

  test('POST /api/v1/crypto/pq/keys/dilithium generates NIST FIPS 204 ML-DSA lattice keypair', async ({ request }) => {
    const response = await request.post('/api/v1/crypto/pq/keys/dilithium');
    expect(response.status()).toBe(200);

    const key = await response.json();
    expect(key.algorithm).toBe('NIST-FIPS-204-ML-DSA-Dilithium3');
    expect(key.keyId).toBeDefined();
    expect(key.seedRhoHex).toHaveLength(64);
    expect(key.publicKeyTHex).toBeDefined();
  });

  test('POST /api/v1/crypto/pq/seal and /verify cryptographically signs and verifies Merkle root', async ({ request }) => {
    const workflowId = 'e2e00000-0000-0000-0000-000000000301';
    const merkleRoot = 'a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e';

    // 1. Create Quantum-Resistant Audit Seal
    const sealResponse = await request.post('/api/v1/crypto/pq/seal', {
      data: {
        workflowId: workflowId,
        merkleRootHash: merkleRoot
      }
    });

    expect(sealResponse.status()).toBe(200);
    const seal = await sealResponse.json();

    expect(seal.workflowId).toBe(workflowId);
    expect(seal.merkleRootHash).toBe(merkleRoot);
    expect(seal.algorithm).toBe('NIST-FIPS-204-ML-DSA-Dilithium3');
    expect(seal.securityCategory).toContain('Quantum-Resistant');

    // 2. Verify Valid Quantum Seal
    const verifyResponse = await request.post('/api/v1/crypto/pq/verify', {
      data: seal
    });

    expect(verifyResponse.status()).toBe(200);
    const verifyResult = await verifyResponse.json();
    expect(verifyResult.valid).toBe(true);

    // 3. Verify Tampered Merkle Root Fails Lattice Signature Check
    const tamperedSeal = {
      ...seal,
      merkleRootHash: 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
    };

    const tamperedResponse = await request.post('/api/v1/crypto/pq/verify', {
      data: tamperedSeal
    });

    const tamperedResult = await tamperedResponse.json();
    expect(tamperedResult.valid).toBe(false);
  });

  test('POST /api/v1/crypto/pq/kyber/encapsulate derives identical symmetric key via NIST FIPS 203 ML-KEM', async ({ request }) => {
    const response = await request.post('/api/v1/crypto/pq/kyber/encapsulate');
    expect(response.status()).toBe(200);

    const result = await response.json();
    expect(result.algorithm).toBe('NIST-FIPS-203-ML-KEM-Kyber768');
    expect(result.ciphertextHex).toBeDefined();
    expect(result.sharedSecretHex).toHaveLength(64); // 256-bit key
    expect(result.decapsulationMatched).toBe(true);
  });
});
