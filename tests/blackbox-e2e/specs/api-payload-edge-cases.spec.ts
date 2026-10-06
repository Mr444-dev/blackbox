import { test, expect } from '@playwright/test';

test.describe('API Payload Edge Cases', () => {

  test('Malformed JSON syntax returns 400 Bad Request', async ({ request }) => {
    const malformedJson = '{"orderId": "ORD-FUZZ", "amount": 123.45, "unclosed": ';

    const response = await request.post('/api/v1/workflows', {
      headers: {
        'Content-Type': 'application/json',
        'X-Idempotency-Key': `FUZZ-SYNTAX-${Date.now()}`
      },
      data: malformedJson
    });

    // ASP.NET Core JSON model binding must catch syntax errors cleanly as 400
    expect(response.status()).toBe(400);
  });

  test('Escaped null characters are rejected before PostgreSQL JSONB persistence', async ({ request }) => {
    const nullByteString = "attack\u0000payload\u0000test";
    const deeplyNested = JSON.stringify({
      level1: { level2: { level3: { level4: { probe: nullByteString } } } }
    });

    const response = await request.post('/api/v1/workflows', {
      headers: {
        'X-Idempotency-Key': `FUZZ-NESTED-${Date.now()}`
      },
      data: {
        payloadJson: deeplyNested
      }
    });

    expect(response.status()).toBe(400);
    const body = await response.json();
    expect(body.error).toContain('must not contain null characters');
  });

  test('Empty and whitespace-only correlation IDs are rejected', async ({ request }) => {
    const response = await request.post('/api/v1/workflows', {
      data: {
        correlationId: '   ',
        payloadJson: '{}'
      }
    });

    expect(response.status()).toBe(400);
    const body = await response.json();
    expect(body.error).toBeDefined();
  });

  test('A 50 KiB payload within the configured limit is accepted', async ({ request }) => {
    // 50 KiB payload
    const largeData = 'A'.repeat(50000);
    const response = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `FUZZ-LARGE-${Date.now()}` },
      data: {
        payloadJson: JSON.stringify({ blob: largeData })
      }
    });

    expect(response.status()).toBe(200);
  });

  test('Workflow payloads larger than 256 KiB are rejected', async ({ request }) => {
    const response = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `FUZZ-OVERSIZE-${Date.now()}` },
      data: { payloadJson: JSON.stringify({ blob: 'A'.repeat(300 * 1024) }) }
    });

    expect(response.status()).toBe(413);
  });
});
