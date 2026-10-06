# Resilience and fault-injection test plan

The repository includes sample chaos configuration under `tests/chaos`, but the local Compose stack does not currently route PostgreSQL, Kafka, or carrier traffic through Toxiproxy. The scenarios below are test objectives, not experiments already performed.

## Proposed scenarios

| Fault | Expected invariant | Evidence to collect |
|---|---|---|
| Kafka unavailable during publish | Committed workflow and Outbox data remain; the worker retries; no false `Published` state is recorded. | PostgreSQL rows, worker logs, broker recovery, delivery count. |
| Process crash after Kafka acknowledgement but before database commit | Event may be delivered again; consumer Inbox deduplication prevents duplicate processing. | Broker offsets, Inbox records, business effect count. |
| PostgreSQL connection loss during reservation | The transaction rolls back; inventory capacity is unchanged. | Database snapshot before and after fault. |
| Carrier timeout | Retry/circuit-breaker behavior follows configured policy and does not claim successful dispatch. | Request count, elapsed time, circuit state, workflow state. |

## Before calling this chaos-tested

Add working Toxiproxy routes and health checks, make fault injection repeatable, assert database and broker outcomes, run the scenarios in CI, and attach raw logs/results. No availability, zero-loss, recovery-time, or performance claim is supported until these runs exist.
