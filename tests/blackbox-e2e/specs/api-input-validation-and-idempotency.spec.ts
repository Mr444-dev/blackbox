import { test, expect } from '@playwright/test';

test.describe('API Input Validation and Idempotency', () => {

  test('A missing workflow returns 404 without database implementation details', async ({ request }) => {
    // This checks a nonexistent ID only; the application has no tenant model.
    const randomForeignId = '00000000-0000-0000-0000-000000000999';
    const response = await request.get(`/api/v1/workflows/${randomForeignId}`);

    // Must return 404 Not Found without leaking internal table schemas or stack traces
    expect(response.status()).toBe(404);
    const body = await response.json();
    expect(body.error).toContain('not found');
    expect(JSON.stringify(body)).not.toContain('Npgsql');
    expect(JSON.stringify(body)).not.toContain('SELECT');
  });

  test('A SQL-like idempotency key is handled as a literal correlation ID', async ({ request }) => {
    const maliciousSqlPayload = "corr-sqli-test'; DROP TABLE workflows; SELECT * FROM users WHERE '1'='1";

    const response = await request.post('/api/v1/workflows', {
      headers: {
        'X-Idempotency-Key': maliciousSqlPayload
      },
      data: {
        payloadJson: '{"safe":"payload"}'
      }
    });

    // The key is stored as data; this does not constitute a complete SQL injection assessment.
    expect(response.status()).toBe(200);
    const workflow = await response.json();
    expect(workflow.correlationId).toBe(maliciousSqlPayload);

    // The API process liveness endpoint remains available.
    const health = await request.get('/health');
    expect(health.status()).toBe(200);
  });

  test('Repeating a create request with the same key returns the original workflow', async ({ request }) => {
    const correlationId = `OWASP-REPLAY-${Date.now()}`;

    // Request 1: Initial authorized submission
    const res1 = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': correlationId },
      data: { payloadJson: '{"transactionAmount":999.00}' }
    });
    expect(res1.status()).toBe(200);
    const w1 = await res1.json();

    // Request 2: Repeat the same idempotent request.
    const res2 = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': correlationId },
      data: { payloadJson: '{"transactionAmount":999.00}' }
    });
    expect(res2.status()).toBe(200);
    const w2 = await res2.json();

    // Idempotency guarantee: exactly identical ID returned, no second transaction created
    expect(w2.id).toBe(w1.id);
  });

  test('A non-positive reservation quantity is rejected by the domain invariant', async ({ request }) => {
    // 1. Create valid workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `OWASP-QTY-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    // 2. Attempt to reserve a negative quantity.
    const reserveResp = await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: {
        resourceId: 'SKU-LAPTOP-001',
        quantity: -50
      }
    });

    // The domain invariant maps to 409 Conflict.
    expect(reserveResp.status()).toBe(409);
    const body = await reserveResp.json();
    expect(body.error).toContain('greater than zero');
  });
});
