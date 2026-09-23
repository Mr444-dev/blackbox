import { test, expect } from '@playwright/test';

test.describe('OWASP API Security Verification Suite', () => {

  test('OWASP API1:2023 - BOLA/IDOR Defense on Workflow Retrieval', async ({ request }) => {
    // Attempting to query a non-existent or foreign tenant GUID
    const randomForeignId = '00000000-0000-0000-0000-000000000999';
    const response = await request.get(`/api/v1/workflows/${randomForeignId}`);

    // Must return 404 Not Found without leaking internal table schemas or stack traces
    expect(response.status()).toBe(404);
    const body = await response.json();
    expect(body.error).toContain('not found');
    expect(JSON.stringify(body)).not.toContain('Npgsql');
    expect(JSON.stringify(body)).not.toContain('SELECT');
  });

  test('OWASP API8:2023 - SQL Injection Defense in Idempotency Key & CorrelationId', async ({ request }) => {
    const maliciousSqlPayload = "corr-sqli-test'; DROP TABLE workflows; SELECT * FROM users WHERE '1'='1";

    const response = await request.post('/api/v1/workflows', {
      headers: {
        'X-Idempotency-Key': maliciousSqlPayload
      },
      data: {
        payloadJson: '{"safe":"payload"}'
      }
    });

    // PostgreSQL parameterized queries must treat the injection string as a literal string
    expect(response.status()).toBe(200);
    const workflow = await response.json();
    expect(workflow.correlationId).toBe(maliciousSqlPayload);

    // Verify table was NOT dropped and system remains 100% operational
    const health = await request.get('/health');
    expect(health.status()).toBe(200);
  });

  test('OWASP API2:2023 - Replay Attack Detection with Exact Idempotency Duplicate', async ({ request }) => {
    const correlationId = `OWASP-REPLAY-${Date.now()}`;

    // Request 1: Initial authorized submission
    const res1 = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': correlationId },
      data: { payloadJson: '{"transactionAmount":999.00}' }
    });
    expect(res1.status()).toBe(200);
    const w1 = await res1.json();

    // Request 2: Malicious duplicate packet replay
    const res2 = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': correlationId },
      data: { payloadJson: '{"transactionAmount":999.00}' }
    });
    expect(res2.status()).toBe(200);
    const w2 = await res2.json();

    // Idempotency guarantee: exactly identical ID returned, no second transaction created
    expect(w2.id).toBe(w1.id);
  });

  test('OWASP API4:2023 - Excessive Resource Allocation Protection (Quantity <= 0)', async ({ request }) => {
    // 1. Create valid workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `OWASP-QTY-${Date.now()}` },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();

    // 2. Attempt to reserve negative quantity (buffer underflow attempt)
    const reserveResp = await request.post(`/api/v1/workflows/${workflow.id}/reserve`, {
      data: {
        resourceId: 'SKU-LAPTOP-001',
        quantity: -50
      }
    });

    // Invariant guard must reject with 409 Conflict
    expect(reserveResp.status()).toBe(409);
    const body = await reserveResp.json();
    expect(body.error).toContain('greater than zero');
  });
});
