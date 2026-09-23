using System.Collections.Concurrent;
using System.Diagnostics;
using Blackbox.Core.Observability;

namespace Blackbox.Infrastructure.Observability;

public readonly record struct EbpfSocketEvent(
    uint Pid,
    string SourceIp,
    string DestinationIp,
    ushort SourcePort,
    ushort DestinationPort,
    double LatencyUs,
    double SocketRttUs,
    uint Retransmits,
    ushort HttpStatus,
    string Method);

public interface IEbpfSocketMetricsBridge
{
    void ProcessKernelSocketEvent(EbpfSocketEvent evt);
    void RecordKernelPacketDrop(string reason = "out_of_buffer");
    IReadOnlyList<EbpfSocketEvent> GetRecentKernelEvents(int limit = 50);
}

public class EbpfSocketMetricsBridge : IEbpfSocketMetricsBridge
{
    private readonly ConcurrentQueue<EbpfSocketEvent> _recentEvents = new();
    private const int MaxHistory = 500;

    public void ProcessKernelSocketEvent(EbpfSocketEvent evt)
    {
        // 1. Record Kernel-Level Metrics into OpenTelemetry / Prometheus
        BlackboxObservability.KernelSocketRttHistogram.Record(
            evt.SocketRttUs,
            new KeyValuePair<string, object?>("dest_port", evt.DestinationPort),
            new KeyValuePair<string, object?>("dest_ip", evt.DestinationIp));

        BlackboxObservability.KernelHttpLatencyHistogram.Record(
            evt.LatencyUs,
            new KeyValuePair<string, object?>("status_code", evt.HttpStatus),
            new KeyValuePair<string, object?>("http_method", evt.Method));

        if (evt.Retransmits > 0)
        {
            BlackboxObservability.KernelTcpRetransmitsCounter.Add(
                evt.Retransmits,
                new KeyValuePair<string, object?>("dest_port", evt.DestinationPort));
        }

        // 2. Buffer in-memory ring representation for diagnostics
        _recentEvents.Enqueue(evt);
        while (_recentEvents.Count > MaxHistory && _recentEvents.TryDequeue(out _))
        {
            // Prune older events to prevent memory unbounded growth
        }
    }

    public void RecordKernelPacketDrop(string reason = "out_of_buffer")
    {
        BlackboxObservability.KernelDropsCounter.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public IReadOnlyList<EbpfSocketEvent> GetRecentKernelEvents(int limit = 50)
    {
        return _recentEvents.Take(Math.Min(limit, MaxHistory)).ToList();
    }
}
