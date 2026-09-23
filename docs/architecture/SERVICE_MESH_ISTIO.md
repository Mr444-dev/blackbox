# Service Mesh Architecture: Istio & Zero-Trust mTLS

BLACKBOX operates in microservices topologies where communication between services (e.g. `blackbox-api` and `blackbox-worker`) must be cryptographically verified, resilient to network anomalies, and capable of zero-downtime canary traffic shifts.

---

## 🏛 Core Service Mesh Capabilities

```
                  ┌──────────────────────────────────────────────┐
                  │          Istio Ingress Gateway               │
                  │   TLS Termination (Port 443 / 80)            │
                  └──────────────────────┬───────────────────────┘
                                         │
                        mTLS (Strict)    ▼
               ┌──────────────────────────────────────────────────┐
               │              blackbox Namespace                  │
               │                                                  │
               │  ┌────────────────────┐  Canary   ┌───────────┐  │
               │  │  blackbox-api:v1   │◄── 90% ───│  Virtual  │  │
               │  │   (version: 1.0.0) │           │  Service  │  │
               │  └────────────────────┘           └─────┬─────┘  │
               │  ┌────────────────────┐                 │        │
               │  │  blackbox-api:v2   │◄── 10% ─────────┘        │
               │  │(version: 2.0.0-can)│                          │
               │  └────────────────────┘                          │
               │            ▲                                     │
               │            │ mTLS + SPIFFE RBAC                  │
               │  ┌─────────┴──────────┐                          │
               │  │  blackbox-worker   │                          │
               │  │ (blackbox-worker-sa)                          │
               │  └────────────────────┘                          │
               └──────────────────────────────────────────────────┘
```

---

## 🔒 1. Mutual TLS (mTLS) & Identity Verification

### PeerAuthentication (Strict Mode)
All intra-cluster TCP traffic within the `blackbox` namespace is intercepted by the Envoy sidecar proxy.
- **Mode**: `STRICT` (all plaintext traffic is actively dropped).
- **SPIFFE ID Formatting**:
  - API Pods: `spiffe://cluster.local/ns/blackbox/sa/blackbox-api-sa`
  - Worker Pods: `spiffe://cluster.local/ns/blackbox/sa/blackbox-worker-sa`
- **Certificate Rotation**: Handled automatically every 24 hours by `istiod` without pod restarts.

---

## 🚦 2. Fine-Grained Traffic Routing & Canary Releases

Defined in [virtual-service.yaml](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/k8s/istio/virtual-service.yaml) and [destination-rule.yaml](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/k8s/istio/destination-rule.yaml):

1. **Canary Weighted Traffic Split**:
   - **90%** of ingress requests route to stable version (`v1: 1.0.0`).
   - **10%** of ingress requests route to canary candidate (`v2: 2.0.0-canary`).
2. **Header-Based Dynamic Override**:
   - Requests carrying `X-Canary: beta` bypass the split and are 100% routed to `v2`.
3. **Resilience & Timeout Budget**:
   - Global HTTP timeout: `3s`.
   - Automatic retry: `attempts: 3`, `perTryTimeout: 1s` on `5xx`, `connect-failure`, and `refused-stream`.
4. **L7 Outlier Detection (Circuit Breaker)**:
   - Pods returning 3 consecutive 5xx errors are ejected from the load-balancing pool for 30 seconds.

---

## 🛡 3. Zero-Trust Pod-to-Pod Authorization (RBAC)

Defined in [authorization-policy.yaml](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/k8s/istio/authorization-policy.yaml):

| Source Principal | Allowed Methods | Target Paths | Purpose |
| :--- | :--- | :--- | :--- |
| Any / Kubelet | `GET` | `/health`, `/healthz`, `/metrics` | Kubernetes probes & Prometheus scraping |
| `istio-ingressgateway-service-account` | `GET`, `POST`, `PUT`, `DELETE` | `/swagger*`, `/api/v1/workflows*`, `/api/v1/resources*` | External public traffic |
| `blackbox-worker-sa` | `GET`, `POST` | `/api/v1/workflows/internal/*`, `/api/v1/dlq/*`, `/api/v1/crdt/*` | Outbox worker callbacks & DLQ replay |
| **All other pods** | **DENIED** | `*` | Zero-Trust default deny posture |

---

## 🚀 4. Deployment Verification Runbook

Apply Istio configurations to any Kubernetes cluster running Istio:

```bash
# 1. Enable Istio sidecar auto-injection for the namespace
kubectl label namespace blackbox istio-injection=enabled --overwrite

# 2. Apply ServiceAccounts and Base Workloads
kubectl apply -f deploy/k8s/istio/service-accounts.yaml
kubectl apply -f deploy/k8s/blackbox-stack.yaml

# 3. Apply Istio Networking & Zero-Trust Policies
kubectl apply -f deploy/k8s/istio/peer-authentication.yaml
kubectl apply -f deploy/k8s/istio/gateway.yaml
kubectl apply -f deploy/k8s/istio/destination-rule.yaml
kubectl apply -f deploy/k8s/istio/virtual-service.yaml
kubectl apply -f deploy/k8s/istio/authorization-policy.yaml

# 4. Verify mTLS status with istioctl
istioctl authn tls-check blackbox-api-service.blackbox.svc.cluster.local
```
