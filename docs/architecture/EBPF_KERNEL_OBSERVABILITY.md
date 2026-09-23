# eBPF Kernel-Level Network Observability (Cilium & Pixie Architecture)

Traditional service mesh architectures (like Istio or Linkerd) inject an Envoy sidecar proxy container into every application pod. While powerful, sidecars impose significant performance penalties:
- **Context Switching**: Each inbound and outbound network packet traverses user-space ➔ kernel-space ➔ Envoy proxy (user-space) ➔ kernel-space ➔ application (user-space), adding **2–4 ms** of tail latency ($p99$).
- **Memory Overhead**: An Envoy sidecar consumes **50–120 MiB RAM** per pod. Across 500 pods, sidecars eat up **25–60 GB** of RAM purely for networking proxies.

**PROJECT BLACKBOX implements zero-overhead sidecarless observability using Linux eBPF (Extended Berkeley Packet Filter) and Cilium Hubble.**

---

## 🏛 Architecture: Kernel-Level Socket Tracing vs Envoy Sidecar

```
   TRADITIONAL SIDECAR (Envoy)                BLACKBOX eBPF (Sidecarless)
   ┌─────────────────────────────┐            ┌─────────────────────────────┐
   │ Pod: User Space             │            │ Pod: User Space             │
   │  ┌──────────┐  ┌──────────┐ │            │  ┌──────────┐               │
   │  │ App Pod  │  │  Envoy   │ │            │  │ App Pod  │               │
   │  │ (.NET)   │  │ Sidecar  │ │            │  │ (.NET)   │               │
   │  └────▲─────┘  └────▲─────┘ │            │  └────▲─────┘               │
   └───────┼─────────────┼───────┘            └───────┼─────────────────────┘
     User/Kernel Context Switches                   Single Context Transition
   ────────┼─────────────┼────────            ────────┼──────────────────────
   │ Linux Kernel        │       │            │ Linux Kernel                 │
   │  ┌────▼─────────────▼────┐  │            │  ┌────▼────────────────────┐ │
   │  │ TCP / Socket Stack    │  │            │  │ eBPF Probe (kprobe/kret)│ │
   │  │ (iptables redirect)   │  │            │  │ [blackbox_socktrace]    │ │
   │  └───────────────────────┘  │            │  └─────────┬───────────────┘ │
   │                             │            │            ▼                 │
   │                             │            │   BPF Ring Buffer / Maps     │
   │                             │            │   (Zero-copy lock-free)      │
   └─────────────────────────────┘            └────────────┬─────────────────┘
                                                           ▼
                                              ┌─────────────────────────────┐
                                              │  EbpfSocketMetricsBridge    │
                                              │  (OpenTelemetry/Prometheus) │
                                              └─────────────────────────────┘
```

---

## 🚀 Key Advantages of eBPF Socket Tracing

| Dimension | Sidecar (Envoy / Istio) | eBPF (Cilium / Tetragon) |
| :--- | :--- | :--- |
| **p99 Tail Latency** | +2.5 ms (due to iptables loops) | **< 0.05 ms (negligible)** |
| **Memory Footprint** | 50–120 MB per pod | **0 MB per pod** (shared host kernel) |
| **CPU Context Switches** | 4 switches per request | **1 switch** |
| **App Pod Injection** | Requires mutating webhook & restart | **Zero pod modification required** |
| **Kernel Crash Safety** | N/A (User space) | **Guaranteed safe by in-kernel BPF Verifier** |

---

## 🛠 Implemented Components in BLACKBOX

1. **Kernel C Probe Program**:
   - [`blackbox_socktrace.bpf.c`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/ebpf/blackbox_socktrace.bpf.c): Attaches to `tcp_sendmsg` and `tcp_recvmsg` to calculate exact kernel round-trip times and track retransmissions.
2. **Cilium Tetragon Tracing Policy**:
   - [`tetragon-http-tracing.yaml`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/k8s/cilium/tetragon-http-tracing.yaml): Enforces kernel security boundaries and traces TCP connection lifecycles on ports 8080, 5432, and 9092.
3. **Cilium Hubble Metrics Helm Config**:
   - [`cilium-ebpf-monitoring.yaml`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/deploy/k8s/cilium/cilium-ebpf-monitoring.yaml): Replaces `kube-proxy` with eBPF and enables Hubble HTTP/TCP metrics export directly to Prometheus.
4. **OpenTelemetry Kernel Bridge**:
   - [`EbpfSocketMetricsBridge.cs`](file:///C:/Users/rusin/.gemini/antigravity-ide/scratch/blackbox/src/Blackbox.Infrastructure/Observability/EbpfSocketMetricsBridge.cs): Consumes raw socket events and translates them into `blackbox_kernel_socket_rtt_us` and `blackbox_kernel_tcp_retransmits_total` Prometheus metrics.

---

## 💼 Interview Talking Points (Staff / Principal Engineer)
- *"While Istio sidecars work well for basic mTLS, at 10,000 req/sec their iptables redirection and multiple user/kernel context switches destroy p99 latency. We introduced Cilium eBPF socket-level tracing to achieve wire-speed observability directly in the kernel with zero sidecars injected into application pods."*
- *"eBPF programs are formally verified for termination and memory safety by the Linux kernel verifier before execution, giving us bare-metal performance without kernel panic risks."*
