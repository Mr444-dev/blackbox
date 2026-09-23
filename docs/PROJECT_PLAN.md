# PROJECT BLACKBOX
## Production-Grade Distributed API & Workflow Reliability Platform

---

### 1. Executive Summary & Vision

**BLACKBOX** is a distributed workflow execution and integration platform designed to solve the hardest problem in modern backend engineering: **guaranteeing strict business state invariants and data consistency across distributed, unreliable external services.**

In enterprise systems (e-commerce, supply chain, fintech, logistics), business processes span multiple systems:
`Order Placed (ERP) ➔ Inventory Reserved (WMS) ➔ Carrier Dispatched (External Logistics) ➔ Payment Captured (Stripe/Adyen) ➔ Fulfillment Completed`.

#### The Fundamental Problem
In real production:
- External provider APIs time out after 5000ms. Did they process the request or not?
- Webhooks arrive out-of-order, delayed, or 3 times in a row due to retry storms.
- Databases commit changes, but message brokers crash before publishing the event (The Dual-Write Problem).
- 50 concurrent buyers attempt to reserve the last remaining stock item simultaneously (Race Conditions & Overselling).
- Upstream services introduce breaking schema changes without warning.

**The BLACKBOX Guarantee:**
> *External dependencies may fail, network packets may be dropped, and requests may duplicate, but the internal system state will never be corrupted, and no side effect will ever be executed twice.*

Testing is not an afterthought in BLACKBOX—**the automated verification engine is a core product deliverable.**

---

### 2. Business Domain & Use Case

**Scenario: Multi-Party Order Fulfillment & Logistics Pipeline**
- **Domain Entities:** Orders, Inventory Allocations, Carrier Shipments, Payment Settlements.
- **Integrated Systems:**
  - **Internal Core**: ERP / Order Orchestrator (`BLACKBOX.Api`).
  - **Warehouse Management System (WMS)**: Local inventory store with strict capacity constraints.
  - **Third-Party Logistics (3PL / Carrier)**: External HTTP API prone to latency spikes, rate limits (HTTP 429), and 5xx outages.
  - **Payment Gateway**: Webhook-based asynchronous payment authorization.

#### The Workflow State Machine Invariants
```
               ┌──────────┐
               │ CREATED  │
               └────┬─────┘
                    │ Validate
                    ▼
               ┌──────────┐
               │VALIDATED │
               └────┬─────┘
                    │ Reserve Inventory (Pessimistic Lock)
                    ▼
               ┌──────────┐      Out of Stock
               │ RESERVED │─────────────────────┐
               └────┬─────┘                     │
                    │ Dispatch Carrier          │
                    ▼                           ▼
            ┌───────────────┐           ┌──────────────┐
            │   WAITING_    │           │    FAILED    │
            │   CALLBACK    │           └──────────────┘
            └───────┬───────┘                   ▲
                    │ Webhook Received / Settle │
                    ▼                           │ Critical Error /
            ┌───────────────┐                   │ Compensated
            │   COMPLETED   │                   │
            └───────────────┘                   │
                    ▲                           │
                    └───────────────────────────┘
```

**Formal Invariants:**
1. A workflow in a terminal state (`COMPLETED`, `FAILED`, `CANCELLED`) can **never** transition back to an active state.
2. An inventory item with `quantity = 1` can be reserved by **exactly one** workflow, even under 100 concurrent requests.
3. Every external side effect must have an associated **Idempotency Key**.
4. Every state mutation must generate an immutable audit log record.

---

### 3. Core Technical Architecture & Pillars

Instead of creating artificial network complexity with 20 premature microservices, BLACKBOX is architected as a high-performance **Modular Monolith** in **C# .NET 10** with asynchronous event decoupling via **Apache Kafka**.

```
┌────────────────────────────────────────────────────────────────────────┐
│                        BLACKBOX PLATFORM (.NET 10)                     │
│                                                                        │
│  ┌──────────────────────┐   ┌───────────────────┐   ┌────────────────┐ │
│  │   HTTP / REST API    │   │  Workflow Engine  │   │  Transactional │ │
│  │  (FastEndpoints/MVC) │──►│ (State Invariants)│──►│     Outbox     │ │
│  └──────────────────────┘   └─────────┬─────────┘   └────────┬───────┘ │
│                                       │                      │         │
└───────────────────────────────────────┼──────────────────────┼─────────┘
                                        ▼                      ▼
                             ┌─────────────────────┐  ┌──────────────────┐
                             │  PostgreSQL (ACID)  │  │   Apache Kafka   │
                             │  - Workflows        │  │  (Event Broker)  │
                             │  - Outbox / Inbox   │  └────────┬─────────┘
                             │  - Inventory Locks  │           │
                             └─────────────────────┘           ▼
┌──────────────────────────────────────────────────────────────┼─────────┐
│  ┌──────────────────────┐   ┌───────────────────┐   ┌────────┴───────┐ │
│  │    External Mock     │◄──│  Outbox Worker &  │◄──│  Idempotent    │ │
│  │   Adapter (Polly)    │   │ Workflow Consumer │   │     Inbox      │ │
│  └──────────────────────┘   └───────────────────┘   └────────────────┘ │
│                                                                        │
│                   DEAD LETTER QUEUE & REPLAY ENGINE                    │
└────────────────────────────────────────────────────────────────────────┘
```

#### Pillar 1: Dual-Write Protection (Transactional Outbox Pattern)
- State changes to business entities and integration event payloads are committed inside a **single ACID transaction** in PostgreSQL.
- A resilient background publisher polls or streams (`outbox_messages`) to Kafka. If Kafka is down, transactions still succeed without data loss; events are delivered once the broker recovers (*At-Least-Once Delivery*).

#### Pillar 2: At-Least-Once Deduplication (Idempotent Consumer / Inbox)
- Every incoming message or external webhook contains an `Idempotency-Key` / `Message-ID`.
- Incoming messages are recorded in an `inbox_messages` table guarded by a unique constraint. Duplicate deliveries are detected instantly, returning the cached outcome without repeating side effects.

#### Pillar 3: Concurrency Control & Race Condition Prevention
- Critical operations (such as inventory allocation) utilize **Pessimistic Locking** (`SELECT ... FOR UPDATE` via Dapper/SQL) and **Optimistic Concurrency** (`xmin` system column / version timestamps).
- Prevents overselling and state corruption under massive concurrent spikes.

#### Pillar 4: Fault Tolerance & Resilience Policies (Polly)
- External integrations are wrapped with:
  - **Circuit Breaker**: Trips upon consecutive failures to protect downstream dependencies.
  - **Exponential Backoff with Jitter**: Avoids thundering herd problem during service recovery.
  - **Fallback & Quarantine**: Poison pills and unrecoverable errors are routed to the **Dead Letter Queue (DLQ)**.

#### Pillar 5: Dead Letter Queue & Deterministic Incident Replay
- Failed workflows capture full error context: stack trace, payload, timestamp, attempt count.
- An administrative Replay API (`POST /api/v1/dlq/{id}/replay`) enables re-evaluating quarantined workflows once fixes are deployed.

---

### 4. Quality Engineering & Testing Strategy

A central highlight of the BLACKBOX platform is its **dual-engine testing harness**: backend integration tests in **C#** and full black-box / end-to-end automation in **TypeScript + Playwright**.

```
                   ▲
                  / \
                 /E2E\             TypeScript + Playwright
                /-----\            (End-to-End Scenarios, Webhooks, Replays)
               /       \
              /Contract \          Pact.io (Consumer-Driven Contracts)
             /-----------\
            / Integration \        C# xUnit + Testcontainers
           /---------------\       (PostgreSQL, Kafka, Real Transactions)
          /  Unit / Domain  \      C# FluentAssertions, State Transitions
         /-------------------\
```

1. **C# Internal Integration (Testcontainers)**:
   - Zero in-memory EF Core mocks. Every integration test spins up real Docker containers for PostgreSQL and Kafka via `Testcontainers-dotnet`.
2. **TypeScript + Playwright API Harness**:
   - Modern QA automation suite verifying REST contracts, idempotency rules, webhook reception, and error status codes.
3. **Pact Consumer-Driven Contract Testing**:
   - Ensures event schemas (`OrderPlacedEvent.v1`) cannot be broken by producer changes without breaking consumer pacts.
4. **k6 Concurrency & Load Stressing**:
   - Validates that concurrent execution against limited inventory yields exactly 1 winner and N rejected requests.
5. **SQL Performance Lab**:
   - PostgreSQL benchmarks with 100,000+ events demonstrating `EXPLAIN ANALYZE` tuning, partial indexes, and execution reduction from seconds to milliseconds.

---

### 5. Technology Stack

| Component | Technology | Purpose |
| :--- | :--- | :--- |
| **Backend Runtime** | **C# / .NET 10** | Modern, high-throughput distributed runtime |
| **Web Framework** | **ASP.NET Core Web API** | REST API endpoints, middleware, filters |
| **Database & ORM** | **PostgreSQL 17**, **EF Core 10**, **Dapper** | Relational integrity, migrations, high-perf raw SQL locks |
| **Messaging Broker** | **Apache Kafka** (confluent-kafka) | Event-driven decoupled communication |
| **Resilience Engine** | **Polly** / .NET Resilience | Retries, Circuit Breakers, Rate Limiting |
| **Testing (Backend)** | **xUnit**, **FluentAssertions**, **Testcontainers** | Containerized real-database integration tests |
| **QA Automation** | **TypeScript**, **Playwright Test** | Modern API testing, black-box orchestration |
| **Contract Testing** | **Pact.io** | Consumer-driven event schema contracts |
| **Load & Stress** | **k6** | Concurrency race condition validation |
| **Containers** | **Docker & Docker Compose** | Reproducible multi-service local environment |

---

### 6. Phased Implementation Roadmap

- **Phase 1 (v0.1) — Core Foundation & Transactional Outbox**
  - Solution structure (.NET 10 Modular Monolith).
  - PostgreSQL schema with Outbox and Inbox tables.
  - State Machine domain entity with strict invariant enforcement.
  - Background Outbox Publisher.
  - C# integration tests with `Testcontainers` (PostgreSQL + Kafka).

- **Phase 2 (v0.2) — External Integration, Resilience & Playwright QA Harness**
  - Unreliable External Partner mock adapter with fault injection.
  - Polly resilience policies (Circuit Breaker, Jittered Backoff).
  - Playwright + TypeScript test suite for API and Webhook scenarios.
  - Pact contract verification tests.

- **Phase 3 (v1.0) — Concurrency Lab, DLQ Replay & Performance Benchmarking**
  - Pessimistic locking implementation for inventory allocation.
  - k6 race condition automated test suite.
  - Dead Letter Queue and Replay API.
  - SQL Performance Lab benchmarks with 100k generated events.
  - Comprehensive documentation, architectural diagrams, and incident runbooks.
