# eBPF observability experiment

## Repository contents

This repository contains a C eBPF source file at `deploy/ebpf/blackbox_socktrace.bpf.c`, Kubernetes examples for Cilium/Tetragon under `deploy/k8s/cilium`, and an API bridge at `src/Blackbox.Infrastructure/Observability/EbpfSocketMetricsBridge.cs`.

The bridge accepts an `EbpfSocketEvent`, records OpenTelemetry instruments, and keeps a bounded in-memory history. The API endpoint accepts event data over HTTP. It does not attach to kernel probes or consume a live eBPF ring buffer by itself.

## Verification status

The eBPF program and Kubernetes manifests have not been built or exercised in the local .NET/Compose verification. No latency, CPU, memory, or packet-loss comparison is published from this repository. The Cilium and Tetragon YAML files are deployment examples, not a supported installation bundle.

## Work required for a live integration

1. Build and load the BPF object for a documented kernel and architecture.
2. Implement a userspace loader/reader with capability checks, lifecycle handling, and bounded backpressure.
3. Define and validate the event schema between the kernel reader and API bridge.
4. Export metrics to an actual collector and document label cardinality and retention.
5. Run tests on a supported Linux kernel and publish reproducible baselines before making performance claims.
