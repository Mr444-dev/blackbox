import { test, expect } from '@playwright/test';

test.describe('Carrier Failure Simulation and DLQ Replay', () => {

  test('Healthy simulator mode returns a tracking number', async ({ request }) => {
    // 1. Create and reserve workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `CB-HEALTHY-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: { resourceId: 'SKU-PHONE-002', quantity: 1 }
    });

    // 2. Dispatch carrier in healthy mode
    const dispatchResp = await request.post(`/api/v1/workflows/${workflow.id}/dispatch`, {
      data: {
        destinationAddress: 'Aleje Jerozolimskie 100, Warszawa',
        mode: 'Healthy'
      }
    });

    expect(dispatchResp.status()).toBe(200);
    const body = await dispatchResp.json();
    expect(body.success).toBe(true);
    expect(body.trackingNumber).toMatch(/^TRK-/);
    expect(body.attemptCount).toBe(1);
    expect(body.status).toBe('WaitingCallback');
  });

  test('Two configured transient failures are retried before simulator success', async ({ request }) => {
    // 1. Create and reserve workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `CB-RETRY-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: { resourceId: 'SKU-PHONE-002', quantity: 1 }
    });

    // 2. Dispatch through a mode that fails twice, then succeeds on attempt three.
    const dispatchResp = await request.post(`/api/v1/workflows/${workflow.id}/dispatch`, {
      data: {
        destinationAddress: 'Rynek Glowny 1, Krakow',
        mode: 'FailTwiceThenSucceed'
      }
    });

    expect(dispatchResp.status()).toBe(200);
    const body = await dispatchResp.json();
    expect(body.success).toBe(true);
    expect(body.attemptCount).toBe(3);
  });

  test('Persistent simulated failures create a DLQ record that can be replayed in healthy mode', async ({ request }) => {
    // 1. Create and reserve workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `CB-TRIP-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: { resourceId: 'SKU-PHONE-002', quantity: 1 }
    });

    // 2. Dispatch through the deterministic failing simulator.
    const dispatchResp = await request.post(`/api/v1/workflows/${workflow.id}/dispatch`, {
      data: {
        destinationAddress: 'Piotrkowska 50, Lodz',
        mode: 'AlwaysFail500'
      }
    });
    expect([400, 503]).toContain(dispatchResp.status());

    // 3. Inspect DLQ
    const dlqResp = await request.get('/api/v1/dlq');
    expect(dlqResp.status()).toBe(200);
    const dlqList = await dlqResp.json();
    expect(dlqList.length).toBeGreaterThan(0);

    const quarantinedItem = dlqList.find((item: any) => item.workflowId === workflow.id);
    expect(quarantinedItem).toBeDefined();
    expect(quarantinedItem.status).toBe('Quarantined');
    expect(quarantinedItem.source).toBe('CarrierDispatch');

    // 4. Replay from DLQ
    const replayResp = await request.post(`/api/v1/dlq/${quarantinedItem.id}/replay`);
    expect(replayResp.status()).toBe(200);
    const replayBody = await replayResp.json();
    expect(replayBody.status).toBe('REPLAYED');
    expect(replayBody.trackingNumber).toMatch(/^TRK-/);
  });
});
