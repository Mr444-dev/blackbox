# 🤖 AI-Driven Autonomous Remediation & Self-Healing Platform

> **LLM-guided root cause diagnosis and automated rollback policy recommendations based on OpenTelemetry incident traces and formal distributed invariants.**

---

## 📌 Mission & Architecture

In modern distributed microservice topologies, cascading failure incidents occur across asynchronous boundaries:
`POST /workflows ➔ PostgreSQL Lock ➔ Kafka Event ➔ Worker ➔ 3PL Carrier ➔ Stripe Payment Webhook`.

When an incident occurs (e.g. downstream payment authorization rejection, carrier 504 timeout, or poisoned schema deserialization), human SRE response times average 15–45 minutes.

**BLACKBOX AI Autonomous Remediation** reduces incident Mean Time to Remediation (MTTR) to **< 200 milliseconds** by:
1. Ingesting correlated OpenTelemetry trace spans, exception traces, and state transition histories.
2. Formulating root cause hypotheses using structured diagnostic reasoning.
3. Generating and executing automated, invariant-safe remediation policies (`RollbackSaga`, `QuarantineToDlq`, `DrainAndRetryWithJitter`).
4. Enforcing strict safety guardrails: high-risk or unrecognized anomalies automatically escalate to human operators (`EscalateToHuman`).

```
                              ┌────────────────────────────────────────┐
                              │     OpenTelemetry Distributed Trace    │
                              │  (API ➔ DB ➔ Kafka ➔ Worker ➔ Partner) │
                              └───────────────────┬────────────────────┘
                                                  │
                                                  ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                        AUTONOMOUS REMEDIATION AGENT (AI CORE)                          │
│                                                                                        │
│  ┌──────────────────────┐   ┌───────────────────────┐   ┌───────────────────────────┐  │
│  │ Telemetry Ingestion  │──►│ Root Cause Hypothesis │──►│ Invariant Policy Engine   │  │
│  │ - Trace Span Latency │   │ - LLM Reasoning       │   │ - Saga Reverse Rollback   │  │
│  │ - Exception Tree     │   │ - Confidence Scoring  │   │ - Circuit Breaker Trip    │  │
│  │ - Active Locks       │   │   (Threshold: 0.90)   │   │ - DLQ Poison Quarantine   │  │
│  └──────────────────────┘   └───────────────────────┘   └─────────────┬─────────────┘  │
│                                                                       │                │
└───────────────────────────────────────────────────────────────────────┼────────────────┘
                                                                        ▼
                                                       ┌────────────────────────────────┐
                                                       │   Safety Guardrail Evaluator   │
                                                       └────────┬──────────────┬────────┘
                                           Confidence >= 0.90   │              │ Confidence < 0.90 /
                                           Known Failure Taxon. │              │ Unrecognized Anomaly
                                                                ▼              ▼
                                                     ┌─────────────┐    ┌───────────────┐
                                                     │  Automated  │    │  Escalate to  │
                                                     │  Execution  │    │  Human (SRE)  │
                                                     │ (0ms delay) │    │  Guardrail    │
                                                     └─────────────┘    └───────────────┘
```

---

## 🎯 Incident Diagnosis Taxonomy

| Incident Type | Telemetry Signature | Automated Remediation Action | Executable Policy |
| :--- | :--- | :--- | :--- |
| **Payment Rejection** | Downstream Payment span returns `CardDeclined` or `InsufficientFunds` | **`RollbackSaga`** | Executes compensating transactions in reverse topological order (`CancelCarrierBooking` ➔ `ReleaseInventory`). Target status: `COMPENSATED`. |
| **Carrier Latency Spike** | Span latency > 3000ms with HTTP 504 / Connection Reset | **`DrainAndRetryWithJitter`** | Tripping circuit breaker to Open, applying exponential backoff with full jitter ($T = \text{random}(0, \min(1500, 100 \times 2^{\text{attempt}}))$). |
| **Poison Pill Payload** | Schema validation failure / Malformed JSON token | **`QuarantineToDlq`** | Routes poisoned event directly to `blackbox.dlq.poison`, suppressing retry storms. |
| **Lock Deadlock** | Concurrency wait queue exhausted on SKU lock | **`ReleaseCapacity`** | Releases orphaned resource lock, restoring available inventory. |
| **Unrecognized Anomaly** | Non-standard exception or security perimeter alert | **`EscalateToHuman`** | Automated execution blocked; status transitioned to `PENDING_HUMAN_APPROVAL`. |

---

## 🚀 REST API Endpoints

- `POST /api/v1/ai/diagnose`: Ingests `IncidentTelemetryContext` and produces structured `RemediationRecommendation`.
- `POST /api/v1/ai/remediate`: Executes recommended remediation policy or enforces human approval guardrail.
