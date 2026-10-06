import { test, expect } from '@playwright/test';

test.describe('Workflow Execution Lifecycle', () => {

  test('application endpoints reject an invalid API key', async ({ request }) => {
    const response = await request.get('/api/v1/workflows', {
      headers: { 'X-API-Key': 'invalid-test-key' }
    });
    expect(response.status()).toBe(401);
  });

  test('GET /health should return 200 OK and Healthy status', async ({ request }) => {
    const response = await request.get('/health');
    expect(response.status()).toBe(200);

    const body = await response.json();
    expect(body.status).toBe('Healthy');
    expect(body.timestamp).toBeDefined();
  });

  test('Complete End-to-End Workflow: Create -> Reserve -> Complete', async ({ request }) => {
    const correlationId = `E2E-LIFECYCLE-${Date.now()}`;
    const payload = JSON.stringify({
      orderId: `ORD-${Date.now()}`,
      customer: 'Enterprise Partner Ltd',
      amount: 1450.00
    });

    // 1. Create Workflow
    const createResponse = await request.post('/api/v1/workflows', {
      headers: {
        'X-Idempotency-Key': correlationId
      },
      data: {
        payloadJson: payload
      }
    });

    expect(createResponse.status()).toBe(200);
    const createdWorkflow = await createResponse.json();
    expect(createdWorkflow.id).toBeDefined();
    expect(createdWorkflow.correlationId).toBe(correlationId);
    expect(createdWorkflow.status).toBe('Created');
    expect(createdWorkflow.version).toBe(1);

    const workflowId = createdWorkflow.id;

    // 2. Fetch Workflow by ID
    const getResponse = await request.get(`/api/v1/workflows/${workflowId}`);
    expect(getResponse.status()).toBe(200);
    const fetchedWorkflow = await getResponse.json();
    expect(fetchedWorkflow.id).toBe(workflowId);
    expect(fetchedWorkflow.status).toBe('Created');

    // 3. Reserve Resource
    const reserveResponse = await request.post(`/api/v1/workflows/${workflowId}/reserve`, {
      data: {
        resourceId: 'SKU-PHONE-002',
        quantity: 1
      }
    });

    expect(reserveResponse.status()).toBe(200);
    const reservation = await reserveResponse.json();
    expect(reservation.resourceId).toBe('SKU-PHONE-002');
    expect(reservation.workflowId).toBe(workflowId);
    expect(reservation.quantity).toBe(1);
    expect(reservation.status).toBe('Active');

    // 4. Complete Workflow
    const completeResponse = await request.post(`/api/v1/workflows/${workflowId}/complete`);
    expect(completeResponse.status()).toBe(200);
    const completedWorkflow = await completeResponse.json();
    expect(completedWorkflow.status).toBe('Completed');
    expect(completedWorkflow.version).toBeGreaterThan(1);
  });
});
