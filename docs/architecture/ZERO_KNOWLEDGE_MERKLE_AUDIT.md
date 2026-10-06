# Merkle audit proofs and public claims

## Current implementation

The audit module hashes workflow event fields into a hash chain, builds Merkle trees from entries, and verifies inclusion paths. A public claim can be reconstructed from non-payload fields plus a committed payload hash and checked against a Merkle proof.

This is a Merkle inclusion proof, **not a zero-knowledge proof**. The verifier receives the public claim and can see its fields. The audit ledger and certified roots are held in process memory; they are not persisted, replicated, externally timestamped, or independently anchored. A process restart loses the ledger.

The API and tests demonstrate hash and proof checks only. They do not provide immutable storage, signer identity, key rotation, a trust chain, or legal-grade audit retention.

## Production work required

- Persist entries and seals with transactional ordering and migrations.
- Anchor signed roots in a separately controlled system.
- Define access control, retention, export, and key-management procedures.
- Add tamper/restart/concurrency integration tests.
- Use a real zero-knowledge protocol and vetted implementation if confidentiality of public claims is a requirement.
