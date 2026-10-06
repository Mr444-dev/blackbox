import { test, expect } from '@playwright/test';

test.describe('In-Memory CRDT Merge Examples', () => {

  test('Two simulated replicas merge counter and register updates deterministically', async ({ request }) => {
    const resourceId = `CRDT-RES-${Date.now()}`;

    // 1. Region A (westeurope) performs local allocations (+50, -10)
    const deltaA1 = await request.post(`/api/v1/crdt/counters/${resourceId}/delta`, {
      data: { regionId: 'westeurope', delta: 50 }
    });
    expect(deltaA1.status()).toBe(200);

    const deltaA2 = await request.post(`/api/v1/crdt/counters/${resourceId}/delta`, {
      data: { regionId: 'westeurope', delta: -10 }
    });
    expect(deltaA2.status()).toBe(200);

    // 2. Region B (polandcentral) concurrently mutates state (+30, -5)
    const deltaB1 = await request.post(`/api/v1/crdt/counters/${resourceId}/delta`, {
      data: { regionId: 'polandcentral', delta: 30 }
    });
    expect(deltaB1.status()).toBe(200);

    const deltaB2 = await request.post(`/api/v1/crdt/counters/${resourceId}/delta`, {
      data: { regionId: 'polandcentral', delta: -5 }
    });
    expect(deltaB2.status()).toBe(200);

    // 3. Verify converged value: (50 - 10) + (30 - 5) = 40 + 25 = 65
    const getRes = await request.get(`/api/v1/crdt/counters/${resourceId}`);
    expect(getRes.status()).toBe(200);
    const body = await getRes.json();
    expect(body.convergedValue).toBe(65);
    expect(body.positive['westeurope']).toBe(50);
    expect(body.negative['westeurope']).toBe(10);
    expect(body.positive['polandcentral']).toBe(30);
    expect(body.negative['polandcentral']).toBe(5);

    // 4. Test Cross-Region Gossip Replication Sync from Region C (eastus)
    const syncRes = await request.post(`/api/v1/crdt/counters/${resourceId}/sync`, {
      data: {
        p: { 'eastus': 100, 'westeurope': 40 }, // westeurope has 40, lower than existing 50 so existing ceiling remains
        n: { 'eastus': 15 }
      }
    });
    expect(syncRes.status()).toBe(200);
    const syncBody = await syncRes.json();
    // Expected: westeurope: (50 - 10) + polandcentral: (30 - 5) + eastus: (100 - 15) = 40 + 25 + 85 = 150
    expect(syncBody.convergedValue).toBe(150);
    expect(syncBody.positive['eastus']).toBe(100);
    expect(syncBody.negative['eastus']).toBe(15);
    expect(syncBody.positive['westeurope']).toBe(50); // Ceil preserved

    // 5. Idempotency test: Re-syncing identical state packet must not alter converged value
    const idempotencyRes = await request.post(`/api/v1/crdt/counters/${resourceId}/sync`, {
      data: {
        p: { 'eastus': 100 },
        n: { 'eastus': 15 }
      }
    });
    const idempotencyBody = await idempotencyRes.json();
    expect(idempotencyBody.convergedValue).toBe(150);
  });
});
