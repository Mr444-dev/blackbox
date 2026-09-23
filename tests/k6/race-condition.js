import http from 'k6/http';
import { check, group } from 'k6';
import { Counter } from 'k6/metrics';

// Custom metrics to verify invariants
const successfulReservations = new Counter('successful_reservations');
const rejectedReservations = new Counter('rejected_reservations');

export const options = {
  scenarios: {
    race_condition_spike: {
      executor: 'per-vu-iterations',
      vus: 50,
      iterations: 1,
      maxDuration: '10s',
    },
  },
  thresholds: {
    // Invariant: Exactly 1 reservation may succeed on 1 stock item!
    'successful_reservations': ['count == 1'],
    'rejected_reservations': ['count == 49'],
  },
};

const BASE_URL = __ENV.BLACKBOX_API_URL || 'http://localhost:5000';
const RESOURCE_ID = 'SKU-LAPTOP-001';

export default function () {
  const correlationId = `K6-RACE-VU${__VU}-${Date.now()}`;

  // Step 1: Create workflow instance
  const createPayload = JSON.stringify({
    payloadJson: JSON.stringify({ vu: __VU, timestamp: new Date().toISOString() }),
  });

  const createParams = {
    headers: {
      'Content-Type': 'application/json',
      'X-Idempotency-Key': correlationId,
    },
  };

  const createRes = http.post(`${BASE_URL}/api/v1/workflows`, createPayload, createParams);
  check(createRes, { 'workflow created': (r) => r.status === 200 });

  const workflow = JSON.parse(createRes.body);

  // Step 2: Attempt concurrent resource reservation
  const reservePayload = JSON.stringify({
    resourceId: RESOURCE_ID,
    quantity: 1,
  });

  const reserveRes = http.post(
    `${BASE_URL}/api/v1/workflows/${workflow.id}/reserve`,
    reservePayload,
    { headers: { 'Content-Type': 'application/json' } }
  );

  if (reserveRes.status === 200) {
    successfulReservations.add(1);
  } else if (reserveRes.status === 409) {
    rejectedReservations.add(1);
  }
}
