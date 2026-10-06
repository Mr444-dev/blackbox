import { test, expect } from '@playwright/test';

test.describe('In-Process Saga Simulation and Compensation', () => {

  test('Successful saga simulation reports its completed steps', async ({ request }) => {
    const correlationId = `SAGA-OK-${Date.now()}`;

    const response = await request.post('/api/v1/workflows/saga', {
      data: {
        correlationId: correlationId,
        resourceId: 'SKU-PHONE-002',
        quantity: 1,
        destinationAddress: 'Florianska 10, Krakow',
        amount: 2500.00,
        simulatePaymentFailure: false
      }
    });

    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body.status).toBe('COMPLETED');
    expect(body.trackingNumber).toMatch(/^TRK-/);
    expect(body.executedSteps).toEqual(['ReserveInventory', 'DispatchCarrier', 'ProcessPayment']);
  });

  test('Simulated payment failure reports compensation in reverse step order', async ({ request }) => {
    const correlationId = `SAGA-FAIL-${Date.now()}`;

    const response = await request.post('/api/v1/workflows/saga', {
      data: {
        correlationId: correlationId,
        resourceId: 'SKU-PHONE-002',
        quantity: 1,
        destinationAddress: 'Krolewska 5, Warszawa',
        amount: 999.00,
        simulatePaymentFailure: true // Triggers step 3 failure
      }
    });

    // Expect 422 Unprocessable Entity because business transaction was compensated
    expect(response.status()).toBe(422);
    const body = await response.json();
    expect(body.status).toBe('COMPENSATED');
    expect(body.failedStep).toBe('ProcessPayment');
    // Compensation must have executed in reverse order: DispatchCarrier then ReserveInventory
    expect(body.compensatedSteps).toEqual(['DispatchCarrier', 'ReserveInventory']);
  });
});
