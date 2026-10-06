# CRDT data structure experiments

## Current implementation

`Blackbox.Core.Crdt` contains a last-write-wins register, a positive-negative counter, and a vector clock. `CrdtReplicationService` stores counters and registers in process memory and exposes merge operations through the API.

These types demonstrate merge behavior. The application does not run a multi-region gossip protocol, persist CRDT state, resolve clock skew, authenticate replica identities, or guarantee cross-region availability. Restarting an API process loses its CRDT data, and separate API replicas do not share it.

## Production work required

- Persist state and define a versioned wire format.
- Authenticate and authorize peer replication.
- Specify conflict semantics, clock behavior, tombstones, and retention.
- Add network partition, duplicate delivery, out-of-order merge, and restart tests.
- Evaluate whether the business data actually permits eventual consistency before applying CRDTs to inventory or orders.

The inventory reservation flow intentionally uses PostgreSQL transactions and row locking; it is not backed by this in-memory CRDT experiment.
