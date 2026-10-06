# Blackbox Engineering Status

**Last reviewed:** 2026-10-06  
**Status:** Portfolio prototype in active development. Not production ready.

## Latest verification attempt

The source, documentation, workflow configuration, and local run instructions were reviewed on 2026-10-06. Results below separate checks that completed from checks blocked by this environment.

| Check | Result |
|---|---|
| `dotnet build Blackbox.slnx --configuration Release --warnaserror` | Passed: all projects built with 0 warnings and 0 errors. |
| .NET unit tests | 71 passed, 0 failed, 0 skipped. |
| PostgreSQL integration tests (`BLACKBOX_TESTCONTAINERS=true`) | 6 passed, 0 failed, 0 skipped using Testcontainers and a reachable Docker Engine. |
| `docker compose config --quiet` | Passed with CI-style placeholder environment values. This validates Compose parsing only; runtime startup is recorded separately below. |
| Playwright TypeScript check | Passed with `npx --no-install tsc --noEmit --pretty false` under Node.js 24.12.0. CI is pinned to Node.js 22. |
| Playwright discovery | 31 tests across 13 spec files were discovered with `BLACKBOX_API_KEY` and `BLACKBOX_API_URL` set. Discovery does not prove the API scenarios pass. |
| Compose stack | API readiness passed. PostgreSQL, Kafka, API, and worker started; the Compose `kafka-init` service created both required topics. |
| Playwright API suite | 31 passed, 0 failed against the local Compose stack. |
| Kafka Outbox/Inbox flow | After the E2E run, PostgreSQL reported 83 Outbox records `Published` and 83 Inbox receipts. A separate workflow smoke request was then observed as `Published` with its Inbox receipt persisted. Both Kafka topics were present. |

An earlier version of this file reported 67 passing unit tests; the current count is 71 after the audit fixes. The latest remote GitHub Actions result has not been checked.

## Implemented core path

- ASP.NET Core API uses an API key authentication scheme and protects application routes with a fallback authorization policy.
- API and worker require explicit database credentials; the API fails startup if its API key is missing or shorter than 32 UTF-8 bytes. API startup also stops if schema creation or seed initialization fails, so readiness cannot hide a database initialization error. Compose reads local secrets from `.env` rather than embedding fallback passwords.
- API-key comparisons hash both values and use a fixed-time comparison; this remains a single shared demo key, not a production identity system.
- In-memory, evaluation, AI-recommendation, and educational cryptography endpoints are mapped only in `Development`; the `Production` environment does not expose those experimental routes.
- Workflow creation and its Outbox event are persisted in one PostgreSQL transaction.
- Correlation IDs have a unique database constraint. Concurrent duplicate requests resolve to the workflow that won the insert race.
- Reusing an idempotency key with a different JSON value is rejected with a conflict. Structural comparison is whitespace- and object-key-order independent, matching PostgreSQL `jsonb` storage semantics.
- Workflow payload validation rejects invalid JSON, U+0000 characters unsupported by PostgreSQL `jsonb`, and payloads larger than 256 KiB before persistence.
- Inventory reservation uses a PostgreSQL row lock and applies domain capacity rules.
- The worker publishes Outbox records through a required Kafka producer. It claims one row with `FOR UPDATE SKIP LOCKED`, holds the transaction through broker acknowledgement, retries failures, and creates a DLQ record after five attempts.
- The Kafka consumer records topic, partition, and offset in the Inbox before committing the Kafka offset. The Inbox currently records receipt only; it does not perform a business operation.
- The local Compose `kafka-init` service creates `blackbox.workflow.events` and `blackbox.resource.events`. A separately managed Kafka cluster must provision these topics before starting the Worker.
- Delivery is at least once. A publish accepted by Kafka followed by a failed database commit can be delivered again.
- The DLQ replay endpoint only handles quarantined `CarrierDispatch` simulator records and now validates their stored workflow ID and destination. Outbox DLQ records return `501` until a safe operator-controlled requeue path is implemented; the carrier simulator must not mark Kafka events as replayed.

## Important boundaries

- The verification scorecard checks basic workflow identifiers, JSON syntax, and evidence supplied to a scenario. It does not validate an API schema or provide formal verification. Missing evidence is marked **not evaluated**; it does not measure production availability, P95 latency, or real security posture.
- The remediation module is deterministic keyword classification. It recommends policies but does not execute them. Unknown cases require human approval.
- Carrier dispatch is an in-process deterministic simulator; it does not call an external carrier API. `ResilientCarrierService` is scoped and builds its Polly pipeline in the constructor, so circuit-breaker state does not persist across requests. There is no state provider or endpoint for observing breaker state. The E2E cases exercise simulator retries and replay, not a production-grade breaker or carrier integration.
- The Kafka Inbox consumer previously stopped the Worker when subscribed topics did not yet exist. Compose now provisions them before the Worker starts. External Kafka deployments must create the topics first; the consumer does not create topics or recover from a permanently missing-topic configuration.
- Carrier DLQ replay does not yet claim the record atomically before dispatch. Concurrent replay requests could both pass the `Quarantined` status check; add an operator-authenticated claim/lock and idempotency before connecting a real side effect.
- The signature and key-encapsulation examples are educational toy algorithms, not ML-DSA/ML-KEM or FIPS implementations. They provide no security guarantees.
- CRDT state and the Merkle audit ledger are in-memory and are not shared across API replicas.
- EF Core `EnsureCreated` is used for the demo schema. There is no migration-based upgrade process, and this bootstrap is not safe for evolving a persistent production database.
- The Compose credentials are for local development only. API key authentication does not provide per-user scopes, rotation, rate limiting, or production identity management.
- OpenTelemetry instrumentation exists in code, but the Compose stack currently has no configured exporter or metrics scrape endpoint.
- Kubernetes, Istio, Cilium, benchmark, and chaos files are examples or experiments. Their performance and runtime claims have not been validated as part of these checks.

## Recommended next milestones

1. Run CI with Docker enabled; fix any Testcontainers, Compose, or Playwright failures and publish the green workflow badge.
2. Add EF migrations and a migration test from an empty PostgreSQL instance.
3. Add Outbox retry timestamps/backoff, retryable vs permanent error classification, and operator-controlled DLQ replay for Kafka events.
4. Extend Inbox processing so a real business effect and Inbox record commit in one PostgreSQL transaction; test redelivery and concurrent consumers.
5. Export metrics/traces to an actual local collector and verify the endpoints with integration tests.
6. Add workflow-specific schema validation, structured error handling for missing workflows, and API contract tests. Payload syntax, null-character, and size checks are already implemented.
7. Add API-level tests proving experimental routes are absent in `Production`, and replace the shared API key with a managed identity/authorization model before any real deployment.

## Files changed for portfolio preparation

See [README](README.md) for the runbook and [crypto demo limitations](docs/architecture/POST_QUANTUM_CRYPTOGRAPHY.md) for the cryptography boundary. This status file records evidence and limitations; it is not a release approval.
