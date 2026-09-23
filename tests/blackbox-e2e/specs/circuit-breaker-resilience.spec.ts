import { test, expect } from '@playwright/test';

test.describe('External Partner Resilience & Circuit Breaker', () => {

  test('Carrier dispatch in Healthy mode returns 200 OK and tracking number', async ({ request }) => {
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

  test('Carrier dispatch with intermittent failures retries and succeeds via Polly', async ({ request }) => {
    // 1. Create and reserve workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `CB-RETRY-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: { resourceId: 'SKU-PHONE-002', quantity: 1 }
    });

    // 2. Dispatch carrier with 3 intermittent failures
    const dispatchResp = await request.post(`/api/v1/workflows/${workflow.id}/dispatch`, {
      data: {
        destinationAddress: 'Rynek Glowny 1, Krakow',
        mode: 'FailThreeTimesThenSucceed'
      }
    });

    expect(dispatchResp.status()).toBe(200);
    const body = await dispatchResp.json();
    expect(body.success).toBe(true);
    expect(body.attemptCount).toBe(3);
  });

  test('Persistent failures trip Circuit Breaker, quarantine to DLQ, and allow Replay', async ({ request }) => {
    // 1. Create and reserve workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `CB-TRIP-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: { resourceId: 'SKU-PHONE-002', quantity: 1 }
    });

    // 2. Dispatch carrier with AlwaysFail500
    await request.post(`/api/v1/workflows/${workflow.id}/dispatch`, {
      data: {
        destinationAddress: 'Piotrkowska 50, Lodz',
        mode: 'AlwaysFail500'
      }
    });

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
