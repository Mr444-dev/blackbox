# Blackbox: Project Direction and Roadmap

> This document describes planned work. For implemented behavior and recent verification evidence, see the root [README](../README.md) and [Engineering Status](../ENGINEERING_STATUS.md).

## Project goal

Build a small, inspectable order workflow service that demonstrates how to protect database state while coordinating asynchronous work with a message broker and a deterministic in-process simulator for unreliable external-service outcomes.

The portfolio should emphasize implemented engineering decisions and reproducible evidence: transactional boundaries, retry and delivery semantics, concurrency controls, fault handling, and tests. Experimental modules remain clearly separated from the core workflow.

## Current core architecture

- ASP.NET Core API with API key authentication.
- PostgreSQL workflow state, resource inventory, Outbox, Inbox, and DLQ records.
- A worker that publishes Outbox records to Kafka, records Inbox receipts, and maintains a transactional workflow-event projection.
- Domain-level workflow transitions and capacity invariants.
- A local Compose environment and automated .NET/Playwright checks.

Current gaps are tracked in [Engineering Status](../ENGINEERING_STATUS.md). The system is a portfolio prototype, not a production platform.

## Roadmap

### 1. Reproducible local run

- Keep configuration in environment variables and provide a safe `.env.example`.
- Validate the complete API, PostgreSQL, Kafka, and worker startup in GitHub Actions.
- Publish only test results produced by the current workflow run.

### 2. Persistence and messaging correctness

- EF Core migrations initialize new databases. Existing `EnsureCreated` databases require the checked baseline-adoption script before the first migrated startup.
- Add durable Outbox scheduling with bounded exponential backoff and clear poison-message handling.
- Fault-inject a worker restart after the database transaction commits but before the Kafka offset commits.
- Add operator-controlled Kafka DLQ replay after an atomic claim and explicit authorization are in place.
- Test duplicate delivery, broker restart, worker concurrency, and process interruption through the live Compose stack.

### 3. Operability and API quality

- Add workflow-specific schema validation, consistent problem responses, and request correlation. Payload syntax, null-character, and size checks are already implemented.
- Export traces and metrics to a local OpenTelemetry collector and test the exported signals.
- Add administrative authorization, key rotation, rate limits, and a documented threat model.

### 4. Portfolio evidence

- Publish architecture and sequence diagrams for flows that exist in code.
- Add an API contract and realistic end-to-end scenarios.
- Publish reproducible load, mutation, or chaos results only after the corresponding test is implemented and run.
- Remove or disable toy cryptography and other demonstration-only APIs in any deployment that handles real data.

## Definition of done for a milestone

Every milestone should include implementation, automated tests, an updated explanation of failure behavior, and a passing CI run. Performance and resilience claims must include the command, environment, raw result artifact, and measurement date.
