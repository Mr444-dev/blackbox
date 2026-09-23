using Blackbox.Infrastructure.Observability;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class EbpfObservabilityTests
{
    [Fact]
    public void ProcessKernelSocketEvent_ShouldRecordMetricsAndMaintainRecentRingBuffer()
    {
        // Arrange
        var bridge = new EbpfSocketMetricsBridge();
        var evt = new EbpfSocketEvent(
            Pid: 1234,
            SourceIp: "10.244.1.15",
            DestinationIp: "10.244.2.42",
            SourcePort: 48922,
            DestinationPort: 8080,
            LatencyUs: 1450.5,
            SocketRttUs: 110.2,
            Retransmits: 0,
            HttpStatus: 200,
            Method: "POST");

        // Act
        bridge.ProcessKernelSocketEvent(evt);

        // Assert
        var recent = bridge.GetRecentKernelEvents(10);
        recent.Should().HaveCount(1);
        recent[0].Pid.Should().Be(1234);
        recent[0].DestinationPort.Should().Be(8080);
        recent[0].LatencyUs.Should().Be(1450.5);
    }

    [Fact]
    public void ProcessKernelSocketEvent_WithTcpRetransmits_ShouldRecordAnomalies()
    {
        // Arrange
        var bridge = new EbpfSocketMetricsBridge();
        var anomalyEvt = new EbpfSocketEvent(
            Pid: 9999,
            SourceIp: "10.244.1.15",
            DestinationIp: "10.244.3.99",
            SourcePort: 54100,
            DestinationPort: 5432,
            LatencyUs: 8900.0,
            SocketRttUs: 2500.0,
            Retransmits: 3,
            HttpStatus: 200,
            Method: "TCP");

        // Act
        bridge.ProcessKernelSocketEvent(anomalyEvt);
        bridge.RecordKernelPacketDrop("ring_buf_overflow");

        // Assert
        var recent = bridge.GetRecentKernelEvents(5);
        recent.Should().ContainSingle(e => e.Retransmits == 3);
    }
}
