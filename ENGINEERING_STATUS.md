# Blackbox Engineering Status

**Last reviewed:** 2026-10-15
**Status:** Portfolio prototype in active development. Not production ready.

## Inbox redelivery PR verification — 2026-10-15

Verified locally from PR #1 head `278f5378802d123183c5dcbd8c2fd67d5999b526` on Windows with .NET SDK 10.0.401 and Docker Desktop 29.2.0.

| Check | Result |
|---|---|
| `dotnet restore Blackbox.slnx` | Passed. |
| `dotnet build Blackbox.slnx --no-restore --configuration Release --warnaserror` | Passed: 0 warnings and 0 errors. |
| `dotnet test tests/Blackbox.UnitTests/Blackbox.UnitTests.csproj --no-build --configuration Release` | Passed: 71/71. |
| `BLACKBOX_TESTCONTAINERS=true dotnet test tests/Blackbox.IntegrationTests/Blackbox.IntegrationTests.csproj --no-build --configuration Release` | Passed: 8/8 PostgreSQL-backed tests, including new redelivery and concurrent-delivery cases. |
| GitHub Actions run #9 and rerun | Not green. The backend job ended before any step ran (`runner_id: 0`, empty step list, about 2 seconds); the logs endpoint returned `BlobNotFound`. The GitHub-side cause is unknown, so CI remains unresolved. |
| Full Kafka/PostgreSQL redelivery E2E | Not run for this change. The new tests exercise the PostgreSQL Inbox processor, not a live Kafka consumer and offset commit. |

That result records the earlier PR-head verification only; the next section records the newer migration and Inbox projection changes. The GitHub Actions failure remains unexplained.

## Inbox projection and migrations — 2026-10-15

Verified in the current PR worktree on Windows with Docker Desktop 29.2.0.

| Check | Result |
|---|---|
| `dotnet build Blackbox.slnx --configuration Release --warnaserror` | Passed: 0 warnings and 0 errors. |
| `dotnet test tests/Blackbox.UnitTests/Blackbox.UnitTests.csproj --no-build --configuration Release` | Passed: 71/71. |
| `BLACKBOX_TESTCONTAINERS=true dotnet test tests/Blackbox.IntegrationTests/Blackbox.IntegrationTests.csproj --no-build --configuration Release` | Passed: 14/14 PostgreSQL-backed tests. Includes empty-database migration/re-migration, baseline adoption with existing-row preservation, Inbox redelivery/concurrency/out-of-order delivery, event-content conflict, malformed JSON, duplicate property names, and per-transport quarantine deduplication. |
| Isolated Docker Compose smoke test | Previously passed with a fresh database: two migrations applied, live Kafka delivery created one Inbox receipt and projection row, and an exact republish at a new offset left both at one. A changed payload with the same EventId and malformed JSON were durably quarantined. This smoke test predates the final transport-identity refinement; its updated semantics are covered by the current PostgreSQL tests. |
| Worker crash between PostgreSQL commit and Kafka offset commit | Not simulated in the live stack. The processor-level test uses a fresh DbContext to model replay; a fault-injected Kafka consumer test remains future work. |
| GitHub Actions run #11 on head `a9932af` | Failed before any steps were recorded. The .NET job had an empty step list, its log endpoint returned `BlobNotFound`, and the Compose job was skipped. The GitHub-side cause is unknown; remote CI remains unresolved. |

The live smoke test used a dedicated Compose project and separate loopback ports; its test containers and volume were removed afterward. It verifies live Kafka delivery, same-ID conflict handling, poison quarantine, and a new-offset replay, but it does not simulate worker interruption between database commit and offset commit or establish a green GitHub workflow. The current test suite additionally confirms that a retry at the same transport offset deduplicates while an identical payload at a different offset remains a separate DLQ record. GitHub Actions run [#11](https://github.com/Mr444-dev/blackbox/actions/runs/38799910247) for the current head failed before recording any steps; its job logs were unavailable and the Compose job was skipped. Earlier run #9 and its rerun also failed before useful step output, so CI remains unresolved.

## Previous full-stack verification — 2026-10-14

Verified on the local Windows working tree with .NET SDK 10.0.401, Docker Desktop 29.2.0, and Node.js 24.12.0. The current verification includes a fix to the local runner so its readiness URL follows Docker Compose's process-environment port override.

| Check | Result |
|---|---|
| `dotnet test Blackbox.slnx --configuration Release` with `BLACKBOX_TESTCONTAINERS=true` | 71 unit tests and 6 PostgreSQL integration tests passed; 0 failed or skipped. |
| `scripts/verify-local.ps1 -KeepStack` | PostgreSQL and Kafka became healthy, API readiness returned `Ready`, and the worker started in a fresh Compose project. |
| Playwright API suite (`npm test`) | 32 passed, 0 failed against that fresh isolated database. Includes Swagger API-key documentation, workflow lifecycle, concurrency, validation, retry, and prototype endpoint scenarios. |
| `node scripts/record-demo.mjs` | Created a workflow, read it back, reserved one inventory unit, and completed it; every API response matched the expected state. Produced a 2:16 WebM and eight annotated screenshots. |
| `npm ci --prefix tests/blackbox-e2e` | Completed; npm reported 0 known vulnerabilities in that test dependency tree. |

An initial runner attempt exposed a port-precedence bug: Compose used the requested alternate port while the runner's readiness URL still targeted the already-running API on port 5000. That attempt reported 27 passes and 5 failures against the wrong, pre-existing instance. I did not reset or remove that existing stack; it may contain data from the attempted test run. The runner fix was followed by the successful 32/32 run against a separate fresh database.

The `.env` contains development-only credentials and is ignored by Git; secret values are not recorded here. The fresh demo stack is still available on loopback at `http://127.0.0.1:55081`; its Compose project is `blackbox-e2e-9a96390d54`. Stop its containers without deleting its database with `docker compose -p blackbox-e2e-9a96390d54 down`.

## Previous verification — 2026-10-06

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
- API and worker require explicit database credentials; the API fails startup if its API key is missing or shorter than 32 UTF-8 bytes. API startup applies EF Core migrations and stops if migration or seed initialization fails, so readiness cannot hide a database initialization error. Compose reads local secrets from `.env` rather than embedding fallback passwords.
- API-key comparisons hash both values and use a fixed-time comparison; this remains a single shared demo key, not a production identity system.
- In-memory, evaluation, AI-recommendation, and educational cryptography endpoints are mapped only in `Development`; the `Production` environment does not expose those experimental routes.
- Workflow creation and its Outbox event are persisted in one PostgreSQL transaction.
- Correlation IDs have a unique database constraint. Concurrent duplicate requests resolve to the workflow that won the insert race.
- Reusing an idempotency key with a different JSON value is rejected with a conflict. Structural comparison is whitespace- and object-key-order independent, matching PostgreSQL `jsonb` storage semantics.
- Workflow payload validation rejects invalid JSON, U+0000 characters unsupported by PostgreSQL `jsonb`, and payloads larger than 256 KiB before persistence.
- Inventory reservation uses a PostgreSQL row lock and applies domain capacity rules.
- The worker publishes Outbox records through a required Kafka producer. It claims one row with `FOR UPDATE SKIP LOCKED`, holds the transaction through broker acknowledgement, retries failures, and creates a DLQ record after five attempts.
- The Kafka consumer uses a valid domain `EventId` as its Inbox identity and falls back to topic/partition/offset for other valid JSON messages. A PostgreSQL unique index and `ON CONFLICT DO NOTHING` handle concurrent duplicate receipts. New domain events update the workflow-event read model in the same transaction as their Inbox receipt; duplicate events do not repeat the effect. Invalid JSON and same-EventId/different-content messages are deduplicated in the DLQ and quarantined before offset commit. The projection is not authoritative workflow status.
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
- EF Core now applies versioned migrations on API startup. Existing databases from the former `EnsureCreated` bootstrap require a backup and the checked baseline-adoption script; mismatched or unknown schemas are rejected. This is still a portfolio prototype, not a production deployment approval.
- The Compose credentials are for local development only. API key authentication does not provide per-user scopes, rotation, rate limiting, or production identity management.
- OpenTelemetry instrumentation exists in code, but the Compose stack currently has no configured exporter or metrics scrape endpoint.
- Kubernetes, Istio, Cilium, benchmark, and chaos files are examples or experiments. Their performance and runtime claims have not been validated as part of these checks.

## Recommended next milestones

1. Diagnose why GitHub Actions jobs are failing before runner startup, then obtain a green workflow run; the local .NET checks pass but remote CI remains unresolved.
2. Exercise redelivery and the durable Inbox projection through the live Kafka/PostgreSQL Compose stack, including a worker restart before Kafka offset commit.
3. Add Outbox retry timestamps/backoff, retryable vs permanent error classification, and operator-controlled DLQ replay for Kafka events.
4. Export metrics/traces to an actual local collector and verify the endpoints with integration tests.
5. Add workflow-specific schema validation, structured error handling for missing workflows, and API contract tests. Payload syntax, null-character, and size checks are already implemented.
6. Add API-level tests proving experimental routes are absent in `Production`, and replace the shared API key with a managed identity/authorization model before any real deployment.

## Files changed for portfolio preparation

See [README](README.md) for the runbook and [crypto demo limitations](docs/architecture/POST_QUANTUM_CRYPTOGRAPHY.md) for the cryptography boundary. This status file records evidence and limitations; it is not a release approval.
