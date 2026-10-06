using Blackbox.Core.Outbox;
using FluentAssertions;
using Xunit;

namespace Blackbox.UnitTests;

public class OutboxMessageTests
{
    [Fact]
    public void RetryableFailure_BelowLimit_ReturnsMessageToPending()
    {
        var message = OutboxMessage.Create("events", "key", "Created.v1", "{}");
        message.MarkProcessing();

        message.MarkRetryableFailure("broker unavailable", maxAttempts: 5);

        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(1);
        message.Error.Should().Be("broker unavailable");
    }

    [Fact]
    public void RetryableFailure_AtLimit_MovesMessageToFailed()
    {
        var message = OutboxMessage.Create("events", "key", "Created.v1", "{}");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            message.MarkProcessing();
            message.MarkRetryableFailure("broker unavailable", maxAttempts: 5);
        }

        message.Status.Should().Be(OutboxStatus.Failed);
        message.RetryCount.Should().Be(5);
    }
}
