import { test, expect } from '@playwright/test';

test.describe('Idempotency & Deduplication Engine', () => {

  test('Concurrent submissions with identical Idempotency-Key return identical workflow ID', async ({ request }) => {
    const idempotencyKey = `IDEM-STORM-${Date.now()}`;
    const payload = JSON.stringify({
      orderId: 'ORD-IDEMPOTENT-STORM',
      amount: 499.00
    });

    // Fire 5 concurrent requests with identical X-Idempotency-Key
    const promises = Array.from({ length: 5 }).map(() =>
      request.post('/api/v1/workflows', {
        headers: {
          'X-Idempotency-Key': idempotencyKey
        },
        data: {
          payloadJson: payload
        }
      })
    );

    const responses = await Promise.all(promises);

    const ids: string[] = [];
    for (const response of responses) {
      expect(response.status()).toBe(200);
      const data = await response.json();
      ids.push(data.id);
      expect(data.correlationId).toBe(idempotencyKey);
    }

    // Invariant: All concurrent requests must resolve to exactly the same single entity
    const uniqueIds = new Set(ids);
    expect(uniqueIds.size).toBe(1);
  });
});
