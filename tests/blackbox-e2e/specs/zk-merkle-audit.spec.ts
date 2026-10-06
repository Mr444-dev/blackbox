import { test, expect } from '@playwright/test';

test.describe('Merkle Audit Inclusion Proofs', () => {

  test('Workflow audit claims should verify against a Merkle inclusion proof', async ({ request }) => {
    const workflowId = 'a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d';

    // 1. Append Event 1: CREATED -> VALIDATED
    const ev1Res = await request.post('/api/v1/audit/events', {
      data: {
        workflowId: workflowId,
        fromState: 'CREATED',
        toState: 'VALIDATED',
        payloadJson: '{"orderId":"ORD-101","sensitivePii":"Confidential Customer Data"}'
      }
    });
    expect(ev1Res.status()).toBe(200);
    const ev1 = await ev1Res.json();
    expect(ev1.entryHash).toHaveLength(64);

    // 2. Append Event 2: VALIDATED -> RESERVED
    const ev2Res = await request.post('/api/v1/audit/events', {
      data: {
        workflowId: workflowId,
        fromState: 'VALIDATED',
        toState: 'RESERVED',
        payloadJson: '{"resourceId":"SKU-LAPTOP-001","qty":1}'
      }
    });
    expect(ev2Res.status()).toBe(200);
    const ev2 = await ev2Res.json();
    // Verify hash chain continuity: ev2.previousEntryHash must match ev1.entryHash
    expect(ev2.previousEntryHash).toBe(ev1.entryHash);

    // 3. Append Event 3: RESERVED -> COMPLETED
    const ev3Res = await request.post('/api/v1/audit/events', {
      data: {
        workflowId: workflowId,
        fromState: 'RESERVED',
        toState: 'COMPLETED',
        payloadJson: '{"paymentStatus":"SETTLED"}'
      }
    });
    expect(ev3Res.status()).toBe(200);
    const ev3 = await ev3Res.json();
    expect(ev3.previousEntryHash).toBe(ev2.entryHash);

    // 4. Seal Block & Commit Merkle Root
    const sealRes = await request.post('/api/v1/audit/seal');
    expect(sealRes.status()).toBe(200);
    const block = await sealRes.json();
    expect(block.merkleRoot).toHaveLength(64);
    expect(block.leafCount).toBeGreaterThanOrEqual(3);

    // 5. Generate Merkle Inclusion Proof for Event 2
    const proofRes = await request.get(`/api/v1/audit/proof/${ev2.entryHash}`);
    expect(proofRes.status()).toBe(200);
    const proof = await proofRes.json();
    expect(proof.leafHash).toBe(ev2.entryHash);
    expect(proof.rootHash).toBe(block.merkleRoot);
    expect(Array.isArray(proof.steps)).toBe(true);

    // 6. Verify Proof against Certified Root
    const verifyRes = await request.post('/api/v1/audit/verify', {
      data: { proof: proof }
    });
    expect(verifyRes.status()).toBe(200);
    const verifyBody = await verifyRes.json();
    expect(verifyBody.valid).toBe(true);

    // 7. Verify a public claim derived from the committed payload hash.
    const zkClaim = {
      sequenceNumber: ev2.sequenceNumber,
      workflowId: ev2.workflowId,
      fromState: ev2.fromState,
      toState: ev2.toState,
      timestampUtc: ev2.timestampUtc,
      payloadHash: ev2.payloadHash, // Public commitment
      previousEntryHash: ev2.previousEntryHash
    };

    const zkRes = await request.post('/api/v1/audit/zk-verify', {
      data: {
        certifiedRoot: block.merkleRoot,
        claim: zkClaim,
        proof: proof
      }
    });
    expect(zkRes.status()).toBe(200);
    const zkBody = await zkRes.json();
    expect(zkBody.valid).toBe(true);
    expect(zkBody.verifiedStateTransition).toBe('VALIDATED -> RESERVED');
    expect(zkBody.proofType).toBe('Merkle inclusion proof');
    expect(zkBody.payloadHandling).toContain('not a zero-knowledge proof');

    // 8. Tamper Resistance: Fake root rejection
    const fakeProof = { ...proof, rootHash: '0000000000000000000000000000000000000000000000000000000000000000' };
    const fakeVerifyRes = await request.post('/api/v1/audit/verify', {
      data: { proof: fakeProof }
    });
    const fakeVerifyBody = await fakeVerifyRes.json();
    expect(fakeVerifyBody.valid).toBe(false);
  });
});
