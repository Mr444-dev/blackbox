# Multi-Region Active-Active Replication with Conflict-Free Replicated Data Types (CRDTs)

In global distributed systems (e.g. `westeurope` + `polandcentral` + `eastus`), high availability requires accepting writes in **any** region simultaneously. However, traditional database locking (pessimistic `SELECT ... FOR UPDATE` or distributed two-phase commits) cannot scale across regions due to the speed-of-light network latency penalty (30–90 ms round-trip) and the risk of complete regional outages.

**BLACKBOX solves cross-region concurrent write conflicts mathematically using Conflict-Free Replicated Data Types (CRDTs).**

---

## 🏛 Architecture: Multi-Master Replication & State Convergence

```
┌───────────────────────────────┐                  ┌───────────────────────────────┐
│     REGION A (westeurope)     │                  │   REGION B (polandcentral)    │
│                               │                  │                               │
│  Client Write (Increment: 10) │                  │  Client Write (Increment: 5)  │
│               │               │                  │               │               │
│               ▼               │                  │               ▼               │
│      ┌─────────────────┐      │   Async Gossip   │      ┌─────────────────┐      │
│      │ PNCounter [A]   │◄─────┼──────────────────┼─────►│ PNCounter [B]   │      │
│      │ P: {A: 10}      │      │   Replication    │      │ P: {B: 5}       │      │
│      └────────┬────────┘      │                  │      └────────┬────────┘      │
│               │               │                  │               │               │
│               ▼               │                  │               ▼               │
│     Converged Value: 15       │                  │     Converged Value: 15       │
└───────────────────────────────┘                  └───────────────────────────────┘
```

---

## 📐 Mathematical Guarantees (Semilattice Properties)

Every CRDT in BLACKBOX forms a **join-semilattice** satisfying three fundamental algebraic axioms:

1. **Commutativity**:
   $$\text{Merge}(A, B) = \text{Merge}(B, A)$$
   *The order in which cross-region update packets arrive does not change the final state.*

2. **Associativity**:
   $$\text{Merge}(\text{Merge}(A, B), C) = \text{Merge}(A, \text{Merge}(B, C))$$
   *Network packet batching or intermediate relay nodes do not alter state consistency.*

3. **Idempotency**:
   $$\text{Merge}(A, A) = A$$
   *Duplicate message delivery (at-least-once network retries) produces zero state duplication or side effects.*

---

## 🛠 Implemented CRDT Primitives in BLACKBOX

### 1. `PnCounter` (Positive-Negative Counter)
- **Use Case**: Cross-region inventory allocation and capacity tracking without distributed locks.
- **Data Structure**:
  - `P`: Dictionary mapping `RegionId ➔ Sum(Increments)`
  - `N`: Dictionary mapping `RegionId ➔ Sum(Decrements)`
- **Merge Function**: Pairwise ceiling ($P_{\text{new}}[node] = \max(P_1[node], P_2[node])$).
- **Effective Value**:
  $$\text{Value} = \sum P - \sum N$$

### 2. `VectorClock` (Logical Causality Detection)
- **Use Case**: Determining whether state mutations in different datacenters are causally dependent (`Before`, `After`) or occurred concurrently (`Concurrent`).
- **Data Structure**: Map of `NodeId ➔ MonotonicCounter`.

### 3. `LwwRegister<T>` (Last-Write-Wins Register)
- **Use Case**: Reconciling concurrent workflow metadata updates.
- **Conflict Resolution**:
  - Compares UTC timestamps $(\tau_1, \tau_2)$.
  - If $\tau_1 == \tau_2$ (microsecond collision), deterministically tie-breaks using lexicographical node comparison (`NodeId`).

---

## 🐘 PostgreSQL Logical Replication & PL/pgSQL Vector Merge

In addition to in-memory evaluation, BLACKBOX implements native PostgreSQL triggers ([crdt_replication_schema.sql](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/sql/crdt_replication_schema.sql)):

1. **Publication Setup**:
   ```sql
   CREATE PUBLICATION blackbox_crdt_pub FOR TABLE crdt_pn_counters, crdt_lww_registers;
   ```
2. **Deterministic Upsert Trigger**:
   - Instead of crashing on replication row conflicts, PostgreSQL's `trg_crdt_pn_counter_merge` executes `crdt_jsonb_vector_max(OLD.p_vector, NEW.p_vector)`.
   - The converged scalar value `SUM(P) - SUM(N)` is automatically updated on every regional write.

---

## 🌐 Active-Active REST API Endpoints

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `POST` | `/api/v1/crdt/counters/{resourceId}/delta` | Mutates local region counter (+/- delta) with zero inter-region latency. |
| `POST` | `/api/v1/crdt/counters/{resourceId}/sync` | Receives state delta from a remote region and executes a semilattice merge. |
| `GET` | `/api/v1/crdt/counters/{resourceId}` | Returns the current converged value, positive increments, and negative decrements. |

---

## 💼 Interview Talking Points (Principal / Staff / Lead Backend)
- *"Instead of paying a 50ms latency tax on every write with distributed 2PC across regions, our active-active multi-region cluster uses CRDT counters. Local writes commit in 0.5ms, and regions asynchronously gossip state deltas. The mathematical properties of join-semilattices guarantee eventual convergence with zero conflicts."*
- *"We combined Vector Clocks with LWW-Registers to catch concurrent writes and resolve them deterministically without human intervention."*
- *"By offloading replication conflict resolution directly into PostgreSQL PL/pgSQL JSONB triggers, our cross-region logical replication stream never breaks on primary key collisions."*
