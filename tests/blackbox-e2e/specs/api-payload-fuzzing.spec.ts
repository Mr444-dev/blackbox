import { test, expect } from '@playwright/test';

test.describe('API Payload Robustness & Fuzzing Suite', () => {

  test('Fuzzing: Malformed JSON syntax must return 400 Bad Request, never 500 unhandled', async ({ request }) => {
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

  test('Fuzzing: Null bytes and deeply nested arrays must not crash runtime', async ({ request }) => {
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

    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body.id).toBeDefined();
  });

  test('Fuzzing: Empty and whitespace-only correlation IDs are rejected', async ({ request }) => {
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

  test('Fuzzing: Ultra-large payload buffer is handled gracefully', async ({ request }) => {
    // 50KB payload
    const largeData = 'A'.repeat(50000);
    const response = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': `FUZZ-LARGE-${Date.now()}` },
      data: {
        payloadJson: JSON.stringify({ blob: largeData })
      }
    });

    expect(response.status()).toBe(200);
  });
});
