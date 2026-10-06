# Incident classification and remediation recommendations

## Current implementation

The `/api/v1/ai/diagnose` endpoint is a deterministic, rule-based prototype. It matches keywords in the supplied exception text and, for the payment rule, the failed-step label. It can also compare the longest supplied span with a demonstration threshold. It returns a fixed demonstration score, a proposed action, a JSON policy, and an explanation; the score is not a calibrated probability. The endpoint does not call an LLM, ingest OpenTelemetry automatically, or inspect live workflow state.

The `/api/v1/ai/remediate` endpoint enforces the human-approval flag. If approval is required, it returns `PENDING_HUMAN_APPROVAL`. Otherwise, it explicitly returns `NOT_EXECUTED`: there are no command handlers connected to saga compensation, retries, inventory release, or DLQ operations. The endpoint must not be treated as an autonomous production remediation system.

## Rule categories

| Input clue | Recommendation | Current behavior |
|---|---|---|
| Payment failure text | `RollbackSaga` | Suggestion only; no compensation runs. |
| Timeout text or a span over 3 seconds | `DrainAndRetryWithJitter` | Suggestion only; no retry schedule is enqueued. |
| Malformed payload or contract text | `QuarantineToDlq` | Suggestion only; no Kafka record is moved. |
| Capacity or lock text | `ReleaseCapacity` | Suggestion only; inventory is not modified. |
| Unrecognized failure | `EscalateToHuman` | Execution is held for human approval. |

## Production work required

- Replace string matching with explicit error codes and typed incident facts.
- Derive recommendations from persisted, authenticated telemetry and current workflow state.
- Calibrate confidence from evaluated data; do not interpret the current constants as probabilities.
- Add authorization, audit records, policy versioning, replay protection, and operator approval for high-impact operations.
- Implement command handlers with idempotency and transactional state changes.
- Add integration tests proving both the intended remediation and the absence of side effects when approval is missing.

The current module is useful for demonstrating a safe boundary between diagnosis and execution. It does not demonstrate AI or self-healing in production.
