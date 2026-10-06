import { test, expect } from '@playwright/test';

test.describe('Educational lattice-style crypto demo (not security reviewed)', () => {

  test('POST /api/v1/crypto/pq/keys/dilithium generates a demo keypair', async ({ request }) => {
    const response = await request.post('/api/v1/crypto/pq/keys/dilithium');
    expect(response.status()).toBe(200);

    const key = await response.json();
    expect(key.algorithm).toBe('BLACKBOX-EDUCATIONAL-LATTICE-SIGNATURE-v1');
    expect(key.keyId).toBeDefined();
    expect(key.seedRhoHex).toHaveLength(64);
    expect(key.publicKeyTHex).toBeDefined();
  });

  test('POST /api/v1/crypto/pq/seal and /verify validates the demo seal and rejects tampering', async ({ request }) => {
    const workflowId = 'e2e00000-0000-0000-0000-000000000301';
    const merkleRoot = 'a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e';

    // 1. Create an educational demo seal.
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
    expect(seal.algorithm).toBe('BLACKBOX-EDUCATIONAL-LATTICE-SIGNATURE-v1');
    expect(seal.securityCategory).toContain('not security reviewed');

    // 2. Verify the demo seal.
    const verifyResponse = await request.post('/api/v1/crypto/pq/verify', {
      data: seal
    });

    expect(verifyResponse.status()).toBe(200);
    const verifyResult = await verifyResponse.json();
    expect(verifyResult.valid).toBe(true);

    // 3. Verify tampering is detected.
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

  test('POST /api/v1/crypto/pq/kyber/encapsulate exercises the educational KEM demo', async ({ request }) => {
    const response = await request.post('/api/v1/crypto/pq/kyber/encapsulate');
    expect(response.status()).toBe(200);

    const result = await response.json();
    expect(result.algorithm).toBe('BLACKBOX-EDUCATIONAL-LATTICE-KEM-v1');
    expect(result.ciphertextHex).toBeDefined();
    expect(result.sharedSecretHex).toHaveLength(64); // 256-bit key
    expect(result.decapsulationMatched).toBe(true);
  });
});
