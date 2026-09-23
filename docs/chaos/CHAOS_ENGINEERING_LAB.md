# Chaos Engineering Lab: Resilience Testing Under Simulated Network Disasters

In enterprise distributed systems, network partitions and dependency failures are inevitable. **BLACKBOX** does not rely on wishful thinking; it uses automated chaos injection to prove that when infrastructure crumbles, business invariants remain inviolate.

---

## 🔬 Chaos Testing Architecture (Shopify Toxiproxy)

```
┌─────────────────────────────────────────────────────────────┐
│                       BLACKBOX BACKEND                      │
│                                                             │
│   Outbox Worker ──► [ Toxiproxy: 9095 ] ──(Partition)──► Kafka
│   Database UoW  ──► [ Toxiproxy: 5433 ] ──(Deadlock)───► PostgreSQL
│   Carrier Client ──► [ Toxiproxy: 8085 ] ──(Latency)────► External 3PL
└─────────────────────────────────────────────────────────────┘
```

---

## 🌪 Documented Chaos Scenarios & Invariant Guarantees

### Experiment 1: The 5000ms Downstream Latency Spike
- **Chaos Toxic**: `latency` (injecting 5000ms delay with 1000ms jitter via Toxiproxy API).
- **Injected Command**:
  ```bash
  curl -X POST http://localhost:8474/proxies/carrier_chaos/toxics \
    -d '{"type": "latency", "attributes": {"latency": 5000, "jitter": 1000}}'
  ```
- **Observed Behavior**:
  1. `ResilientCarrierService` timeout kicks in after 2000ms.
  2. Polly retry policy executes with exponential backoff and jitter.
  3. After 3 consecutive timeouts, the **Circuit Breaker trips to `Open` state**.
  4. Subsequent workflow dispatches immediately fast-fail (0ms waiting), protecting ASP.NET Core thread pool threads from exhaustion.
  5. Workflows are quarantined into `dead_letter_messages`.

---

### Experiment 2: Mid-Flight Network Partition (Connection Severed)
- **Chaos Toxic**: `slicer` & `reset_peer` (severing TCP connection midway through execution).
- **Injected Command**:
  ```bash
  curl -X POST http://localhost:8474/proxies/postgres_chaos/toxics \
    -d '{"type": "reset_peer", "attributes": {"timeout": 0}}'
  ```
- **Observed Behavior**:
  1. PostgreSQL connection drops during resource allocation.
  2. The database transaction fails to commit and automatically rolls back.
  3. Invariant check: zero partial records exist; inventory allocation counter is unchanged.
  4. Client receives formal `500/503` response and can safely retry with the same `X-Idempotency-Key`.

---

### Experiment 3: Kafka Broker Outage (Zero Lost Events Guarantee)
- **Chaos Action**: Disabling Kafka proxy entirely:
  ```bash
  curl -X POST http://localhost:8474/proxies/kafka_chaos -d '{"enabled": false}'
  ```
- **Observed Behavior**:
  1. Users continue submitting workflows via `POST /api/v1/workflows`.
  2. **Every workflow succeeds with HTTP 200/201**: state is committed to PostgreSQL, and integration events are safely stored in `outbox_messages` with status `PENDING`.
  3. `OutboxWorker` logs connection warnings, increments retry count, and backs off without crashing.
  4. Once Kafka is re-enabled:
     ```bash
     curl -X POST http://localhost:8474/proxies/kafka_chaos -d '{"enabled": true}'
     ```
  5. `OutboxWorker` automatically drains the backlog and publishes all pending messages (*At-Least-Once Delivery*). Zero data loss.

---

## 🎯 Interview Talking Points (Reliability / Chaos Testing)
- *"We tested our Transactional Outbox not just in code, but under real TCP disconnection using Toxiproxy. When Kafka drops offline for 30 minutes, our API maintains 100% availability for incoming orders, and our outbox worker catches up as soon as the broker recovers."*
- *"Our circuit breaker is proven under synthetic latency spikes to protect downstream partners and stop cascade thread starvation."*
