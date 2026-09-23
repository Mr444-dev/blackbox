import { test, expect } from '@playwright/test';

test.describe('High-Concurrency Race Condition Lab (Pessimistic Locking Verification)', () => {

  test('50 concurrent checkout requests against 1 available stock item: Exactly 1 succeeds, 49 rejected', async ({ request }) => {
    // 1. Create 50 separate workflows in parallel
    const concurrencyLevel = 50;
    const resourceId = 'SKU-LAPTOP-001'; // Resource with AvailableQuantity = 1

    const createPromises = Array.from({ length: concurrencyLevel }).map((_, index) =>
      request.post('/api/v1/workflows', {
        headers: { 'X-Idempotency-Key': `RACE-WF-${Date.now()}-${index}` },
        data: { payloadJson: JSON.stringify({ buyerIndex: index }) }
      })
    );

    const createResponses = await Promise.all(createPromises);
    const workflows = await Promise.all(createResponses.map(r => r.json()));

    expect(workflows.length).toBe(concurrencyLevel);

    // 2. Fire simultaneous reservation requests in a burst race condition
    const reservationPromises = workflows.map(wf =>
      request.post(`/api/v1/workflows/${wf.id}/reserve`, {
        data: {
          resourceId: resourceId,
          quantity: 1
        }
      })
    );

    const reservationResponses = await Promise.all(reservationPromises);

    // 3. Count outcomes: exactly 1 must succeed (200 OK), rest must be rejected (409 Conflict)
    let successCount = 0;
    let conflictCount = 0;

    for (const res of reservationResponses) {
      if (res.status() === 200) {
        successCount++;
        const body = await res.json();
        expect(body.status).toBe('Active');
      } else if (res.status() === 409) {
        conflictCount++;
        const body = await res.json();
        expect(body.error).toContain('InsufficientResourceCapacity');
      }
    }

    // Invariant Proof:
    // Under PostgreSQL pessimistic lock (SELECT ... FOR UPDATE), NO overselling is possible!
    expect(successCount).toBe(1);
    expect(conflictCount).toBe(concurrencyLevel - 1);
  });
});
