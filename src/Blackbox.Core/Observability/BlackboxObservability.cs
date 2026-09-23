using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Blackbox.Core.Observability;

public static class BlackboxObservability
{
    public const string ServiceName = "Blackbox.Platform";
    public const string ServiceVersion = "1.0.0";

    // 1. Tracing ActivitySource (Distributed Spans & Correlation IDs)
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    // 2. Metrics Meter (SLO Tracking & Prometheus/Grafana Dashboards)
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static readonly Counter<long> WorkflowsCreatedCounter = 
        Meter.CreateCounter<long>("blackbox_workflows_created_total", "count", "Total number of workflows initialized");

    public static readonly Counter<long> WorkflowsCompletedCounter = 
        Meter.CreateCounter<long>("blackbox_workflows_completed_total", "count", "Total number of workflows successfully completed");

    public static readonly Counter<long> WorkflowsFailedCounter = 
        Meter.CreateCounter<long>("blackbox_workflows_failed_total", "count", "Total number of failed workflows");

    public static readonly Counter<long> DeadLetterQuarantinedCounter = 
        Meter.CreateCounter<long>("blackbox_dlq_quarantined_total", "count", "Total number of messages routed to DLQ");

    public static readonly Counter<long> InvariantViolationsCounter = 
        Meter.CreateCounter<long>("blackbox_invariant_violations_total", "count", "Total invariant violations detected");

    public static readonly Histogram<double> ReservationDurationMs = 
        Meter.CreateHistogram<double>("blackbox_resource_reservation_duration_ms", "ms", "Latency of resource reservation with row lock");

    // 3. eBPF Kernel Socket Observability Metrics (Sidecarless L4/L7 Tracing)
    public static readonly Histogram<double> KernelSocketRttHistogram = 
        Meter.CreateHistogram<double>("blackbox_kernel_socket_rtt_us", "us", "TCP socket round-trip time directly measured by eBPF probe in kernel space");

    public static readonly Histogram<double> KernelHttpLatencyHistogram = 
        Meter.CreateHistogram<double>("blackbox_kernel_http_latency_us", "us", "HTTP transaction duration measured at kernel socket boundaries");

    public static readonly Counter<long> KernelTcpRetransmitsCounter = 
        Meter.CreateCounter<long>("blackbox_kernel_tcp_retransmits_total", "count", "Kernel-level TCP retransmissions intercepted by BPF tracepoint");

    public static readonly Counter<long> KernelDropsCounter = 
        Meter.CreateCounter<long>("blackbox_kernel_dropped_packets_total", "count", "Packets dropped in kernel networking stack");
}

