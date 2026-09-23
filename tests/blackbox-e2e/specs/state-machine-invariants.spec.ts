import { test, expect } from '@playwright/test';

test.describe('State Machine Invariant Violations', () => {

  test('Missing correlationId and X-Idempotency-Key should return 400 Bad Request', async ({ request }) => {
    const response = await request.post('/api/v1/workflows', {
      data: {
        payloadJson: '{"order":"invalid"}'
      }
    });

    expect(response.status()).toBe(400);
    const body = await response.json();
    expect(body.error).toContain('X-Idempotency-Key header or correlationId');
  });

  test('Attempting to complete a completed workflow should return 409 Conflict', async ({ request }) => {
    const correlationId = `INVARIANT-TERM-${Date.now()}`;

    // 1. Create and complete workflow
    const createResp = await request.post('/api/v1/workflows', {
      headers: { 'X-Idempotency-Key': correlationId },
      data: { payloadJson: '{}' }
    });
    const workflow = await createResp.json();
    const workflowId = workflow.id;

    // Transition to Completed
    const completeResp1 = await request.post(`/api/v1/workflows/${workflowId}/complete`);
    expect(completeResp1.status()).toBe(200);

    // 2. Attempt illegal transition on terminal state
    const completeResp2 = await request.post(`/api/v1/workflows/${workflowId}/complete`);
    expect(completeResp2.status()).toBe(409);
    const errorBody = await completeResp2.json();
    expect(errorBody.error).toContain('TerminalStateImmutability');
  });
});
